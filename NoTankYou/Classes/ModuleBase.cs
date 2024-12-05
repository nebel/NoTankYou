﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Text.Json.Serialization;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;
using ImGuiNET;
using KamiLib.Classes;
using KamiLib.CommandManager;
using KamiLib.Configuration;
using KamiLib.Extensions;
using NoTankYou.Localization;
using NoTankYou.PlayerDataInterface;
using Action = Lumina.Excel.Sheets.Action;

namespace NoTankYou.Classes;

public abstract class ModuleBase {
    public abstract ModuleName ModuleName { get; }
    public abstract void Load();
    public abstract void Unload();
    public abstract void DrawConfigUi();
    public abstract void EvaluateWarnings();
    public virtual void ZoneChange(uint newZoneId) { }
    public List<WarningState> ActiveWarningStates { get; } = [];
    public bool HasWarnings => ActiveWarningStates.Count != 0;
    
    public abstract bool IsEnabled { get; }
}

public abstract unsafe class ModuleBase<T> : ModuleBase, IDisposable where T : ModuleConfigBase, new() {
    protected T Config { get; private set; } = new();
    
    public override bool IsEnabled => Config.Enabled;
    
    protected abstract string DefaultWarningText { get; }
    
    protected string ExtraWarningText { get; set; } = string.Empty;
    
    private readonly HashSet<ulong> suppressedObjectIds = [];
    
    private readonly Dictionary<ulong, Stopwatch> suppressionTimer = new();
    
    private static AtkUnitBase* NameplateAddon 
        => (AtkUnitBase*)Service.GameGui.GetAddonByName("NamePlate");
    
    private readonly DeathTracker deathTracker = new();
    
    public virtual void Dispose() { }
    
    private string ModuleCommand => ModuleName.ToString();

    protected ModuleBase() {
        System.CommandManager.RegisterCommand(new ToggleCommandHandler {
            DisableDelegate = DisableModuleHandler,
            EnableDelegate = EnableModuleHandler,
            ToggleDelegate = ToggleModuleHandler,
            BaseActivationPath = $"/{ModuleCommand}",
        });
        
        System.CommandManager.RegisterCommand(new CommandHandler {
            Delegate = SuppressModuleHandler,
            ActivationPath = $"/{ModuleCommand}/suppress",
        });
    }
    
    protected abstract bool ShouldEvaluate(IPlayerData playerData);
    
    protected abstract void EvaluateWarnings(IPlayerData playerData);

    public override void EvaluateWarnings() {
        ActiveWarningStates.Clear();

        if (!Config.Enabled) return;

        var nameplateAddon = NameplateAddon;
        if (nameplateAddon is null) return;
        if (!nameplateAddon->IsReady) return;
        if (!nameplateAddon->IsVisible) return;

        if (Service.ClientState.IsPvPExcludingDen) return;
        if (System.BlacklistController.IsZoneBlacklisted(Service.ClientState.TerritoryType)) return;
        if (System.SystemConfig.WaitUntilDutyStart && Service.Condition.IsBoundByDuty() && !Service.DutyState.IsDutyStarted) return;
        if (Config.DutiesOnly && !Service.Condition.IsBoundByDuty() && !Config.OptionDisableFlags.HasFlag(OptionDisableFlags.DutiesOnly)) return;
        if (Config.DisableInSanctuary && TerritoryInfo.Instance()->InSanctuary && !Config.OptionDisableFlags.HasFlag(OptionDisableFlags.Sanctuary)) return;
        if (Service.Condition.IsCrossWorld()) return;
        if (System.SystemConfig.HideInQuestEvent && Service.Condition.IsInCutsceneOrQuestEvent()) return;

        var groupManager = GroupManager.Instance();
        
        if (Config.SoloMode || groupManager->MainGroup.MemberCount is 0) {
            if (Service.ClientState.LocalPlayer is not { } player) return;
            
            var localPlayer = (Character*) player.Address;
            if (localPlayer is null) return;
            
            ProcessPlayer(new CharacterPlayerData(localPlayer));
        }
        else {
            foreach (var partyMember in PartyMemberSpan.PointerEnumerator()) {
                ProcessPlayer(new PartyMemberPlayerData(partyMember));
            }
        }
    }
    
    private void ProcessPlayer(IPlayerData player) {
        if (player.GetEntityId() is 0xE0000000 or 0) return;
        if (HasDisallowedCondition()) return;
        if (HasDisallowedStatus(player)) return;
        if (deathTracker.IsDead(player)) return;
        if (!ShouldEvaluate(player)) return;
        if (suppressedObjectIds.Contains(player.GetEntityId())) return;

        EvaluateWarnings(player);
        EvaluateAutoSuppression(player);
    }

    private void EvaluateAutoSuppression(IPlayerData player) {
        if (Config.AutoSuppress) {
            if (Service.ClientState.LocalPlayer is { EntityId: var playerEntityId } && playerEntityId == player.GetEntityId()) {
                return; // Do not allow auto suppression for the user.
            }
            
            suppressionTimer.TryAdd(player.GetEntityId(), Stopwatch.StartNew());
            if (suppressionTimer.TryGetValue(player.GetEntityId(), out var timer)) {
                if (HasWarnings) {
                    if (timer.Elapsed.TotalSeconds >= Config.AutoSuppressTime) {
                        suppressedObjectIds.Add(player.GetEntityId());
                        Service.Log.Warning($"[{ModuleName}]: Adding {player.GetName()} to auto-suppression list");
                    }
                }
                else {
                    timer.Restart();
                }
            }
        }
    }

    private bool HasDisallowedStatus(IPlayerData player)
        => player.HasStatus(1534);

    private static bool HasDisallowedCondition()
        => Service.Condition.Any(ConditionFlag.Jumping61,
            ConditionFlag.Transformed,
            ConditionFlag.InThisState89);

    public override void DrawConfigUi() {
        var minDimension = MathF.Min(ImGui.GetContentRegionMax().X, ImGui.GetContentRegionMax().Y);
        var moduleInfo = ModuleName.GetAttribute<ModuleIconAttribute>()!;
        ImGui.Image(Service.TextureProvider.GetFromGameIcon(moduleInfo.ModuleIcon).GetWrapOrEmpty().ImGuiHandle, new Vector2(minDimension), Vector2.Zero, Vector2.One, KnownColor.White.Vector() with { W = 0.20f });
        ImGui.SetCursorPos(Vector2.Zero);
        
        Config.DrawConfigUi();
    }

    public override void Load() {
        Service.Log.Debug($"[{ModuleName}] Loading Module");
        Config = ModuleConfigBase.Load<T>(ModuleName);
    }

    public override void Unload() {
        Service.Log.Debug($"[{ModuleName}] Unloading Module");
    }
    
    public void ZoneChange(ushort _)
        => suppressedObjectIds.Clear();

    protected static Span<PartyMember> PartyMemberSpan 
        => GroupManager.Instance()->MainGroup.PartyMembers[..GroupManager.Instance()->MainGroup.MemberCount];

    protected void AddActiveWarning(uint actionId, IPlayerData playerData) => ActiveWarningStates.Add(new WarningState {
        Priority = Config.Priority,
        IconId = Service.DataManager.GetExcelSheet<Action>().GetRow(actionId).Icon,
        IconLabel = Service.DataManager.GetExcelSheet<Action>().GetRow(actionId).Name.ToString(),
        Message = (Config.CustomWarning ? Config.CustomWarningText : DefaultWarningText) + ExtraWarningText,
        SourcePlayerName = playerData.GetName(),
        SourceEntityId = playerData.GetEntityId(),
        SourceModule = ModuleName,
    });

    protected void AddActiveWarning(uint iconId, string iconLabel, IPlayerData playerData) => ActiveWarningStates.Add(new WarningState {
        Priority = Config.Priority,
        IconId = iconId,
        IconLabel = iconLabel,
        Message = (Config.CustomWarning ? Config.CustomWarningText : DefaultWarningText) + ExtraWarningText,
        SourcePlayerName = playerData.GetName(),
        SourceEntityId = playerData.GetEntityId(),
        SourceModule = ModuleName,
    });
    
    private void EnableModuleHandler(params string[] args) {
        if (!Service.ClientState.IsLoggedIn) return;
        
        Config.Enabled = true;
        PrintConfirmation();
        Config.Save();
    }
    
    private void DisableModuleHandler(params string[] args) {
        if (!Service.ClientState.IsLoggedIn) return;

        Config.Enabled = false;
        PrintConfirmation();
        Config.Save();
    }
    
    private void ToggleModuleHandler(params string[] args) {
        if (!Service.ClientState.IsLoggedIn) return;
        
        Config.Enabled = !Config.Enabled;
        PrintConfirmation();
        Config.Save();
    }

    private void SuppressModuleHandler(params string[] args) {
        if (!Service.ClientState.IsLoggedIn) return;

        foreach (var warningPlayer in ActiveWarningStates) {
            suppressedObjectIds.Add(warningPlayer.SourceEntityId);
            Service.Chat.Print(Strings.Command, string.Format(Strings.SuppressingWarnings, ModuleName.GetDescription(), warningPlayer.SourcePlayerName));
        }
    }

    private void PrintConfirmation() 
        => Service.Chat.Print(Strings.Command, Config.Enabled ? $"{Strings.Enabling} {ModuleName.GetDescription()}" : $"{Strings.Disabling} {ModuleName.GetDescription()}");
}

[Flags]
public enum OptionDisableFlags {
    None = 0,
    SoloMode = 1 << 1,
    DutiesOnly = 1 << 2,
    Sanctuary = 1 << 3,
}

public abstract class ModuleConfigBase(ModuleName moduleName) {
    public bool Enabled;
    public bool SoloMode;
    public bool DutiesOnly = true;
    public bool DisableInSanctuary = true;
    public int Priority;
    public bool CustomWarning;
    public string CustomWarningText = string.Empty;
    public bool AutoSuppress;
    public int AutoSuppressTime = 60;

    [JsonIgnore] protected bool ConfigChanged { get; set; }
    [JsonIgnore] public virtual OptionDisableFlags OptionDisableFlags => OptionDisableFlags.None;

    public virtual void DrawConfigUi() {
        ImGuiTweaks.Header(Strings.General);
        using (var _ = ImRaii.PushIndent()) {
            ConfigChanged |= ImGui.Checkbox(Strings.Enable, ref Enabled);
            
            using (var disable = ImRaii.Disabled(OptionDisableFlags.HasFlag(OptionDisableFlags.SoloMode))) {
                ConfigChanged |= ImGuiTweaks.Checkbox(Strings.SoloMode, ref SoloMode, Strings.SoloModeHelp);
                DisabledSettingTooltip(disable);
            }
            
            using (var disable = ImRaii.Disabled(OptionDisableFlags.HasFlag(OptionDisableFlags.DutiesOnly))) {
                ConfigChanged |= ImGuiTweaks.Checkbox(Strings.DutiesOnly, ref DutiesOnly, Strings.DutiesOnlyHelp);
                DisabledSettingTooltip(disable);
            } 
            
            using (var disable = ImRaii.Disabled(OptionDisableFlags.HasFlag(OptionDisableFlags.Sanctuary))) {
                ConfigChanged |= ImGuiTweaks.Checkbox(Strings.HideInSanctuary, ref DisableInSanctuary, Strings.HideInSanctuaryHelp);
                DisabledSettingTooltip(disable);
            } 
            
            ConfigChanged |= ImGuiTweaks.PriorityInt(Service.PluginInterface, Strings.Priority, ref Priority);
        }

        if (!OptionDisableFlags.HasFlag(OptionDisableFlags.SoloMode)) {
            ImGuiTweaks.Header("Warning Suppression");
            using (ImRaii.PushIndent()) {
                ConfigChanged |= ImGuiTweaks.Checkbox("Auto Suppress", ref AutoSuppress, $"Automatically suppress warnings for other players after {AutoSuppressTime} Seconds");

                ImGui.PushItemWidth(50.0f * ImGuiHelpers.GlobalScale);
                ConfigChanged |= ImGui.InputInt("Suppression Time (Seconds)", ref AutoSuppressTime, 0, 0);
            }
        }

        ImGuiTweaks.Header(Strings.DisplayOptions);
        using (var _ = ImRaii.PushIndent()) {
            ConfigChanged |= ImGui.Checkbox(Strings.CustomWarning, ref CustomWarning);
            
            ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X);
            ConfigChanged |= ImGui.InputTextWithHint("##Custom_Warning_Text", Strings.WarningText, ref CustomWarningText, 1024);
        } 
        
        ImGuiTweaks.Header(Strings.ModuleOptions);
        using (var _ = ImRaii.PushIndent()) {
            ImGuiHelpers.ScaledDummy(5.0f);
            DrawModuleConfig();
        }

        if (ConfigChanged) {
            Service.Log.Verbose($"Saving config for {moduleName}");
            Save();
            ConfigChanged = false;
        }
    }

    protected void DisabledSettingTooltip(ImRaii.IEndObject endObject) {
        if (endObject.Success && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) {
            using var fullAlpha = ImRaii.PushStyle(ImGuiStyleVar.Alpha, 1.0f);
            ImGui.SetTooltip("This setting is being ignored");
        }
    }

    protected virtual void DrawModuleConfig() {
        ImGui.TextColored(KnownColor.Orange.Vector(), "No additional options for this module");
    }
	
    public static T Load<T>(ModuleName moduleName) where T : new()
        => Service.PluginInterface.LoadCharacterFile(Service.ClientState.LocalContentId, $"{moduleName}.config.json", () => new T());
	
    public void Save()
        => Service.PluginInterface.SaveCharacterFile(Service.ClientState.LocalContentId, $"{moduleName}.config.json", this);
}