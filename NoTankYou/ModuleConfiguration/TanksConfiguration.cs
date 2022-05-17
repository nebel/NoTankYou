﻿using Dalamud.Interface;
using ImGuiNET;
using ImGuiScene;
using NoTankYou.Data.Components;
using NoTankYou.Data.Modules;
using NoTankYou.Enums;
using NoTankYou.Interfaces;
using NoTankYou.Localization;
using NoTankYou.Utilities;

namespace NoTankYou.ModuleConfiguration
{
    internal class TanksConfiguration : IConfigurable
    {
        public ModuleType ModuleType => ModuleType.Tanks;
        public string ConfigurationPaneLabel => Strings.Modules.Tank.ConfigurationPanelLabel;
        public string AboutInformationBox => Strings.Modules.Tank.Description;
        public string TechnicalInformation => Strings.Modules.Tank.TechnicalDescription;
        public TextureWrap? AboutImage { get; }
        public GenericSettings GenericSettings => Settings;
        private static TankModuleSettings Settings => Service.Configuration.ModuleSettings.Tank;

        public TanksConfiguration()
        {

        }

        public void DrawTabItem()
        {
            ImGui.TextColored(Settings.Enabled ? Colors.SoftGreen : Colors.SoftRed, Strings.Modules.Tank.Label);
        }

        public void DrawOptions()
        {
        }
    }
}
