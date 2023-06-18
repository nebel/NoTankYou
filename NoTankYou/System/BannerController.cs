﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Numerics;
using Dalamud.Interface;
using ImGuiNET;
using KamiLib.AutomaticUserInterface;
using NoTankYou.DataModels;
using NoTankYou.Models;
using NoTankYou.Utilities;
using NoTankYou.Views.Components;

namespace NoTankYou.System;

public class BannerController : IDisposable
{
    private BannerConfig config = new();
    private Vector2? holdOffset;

    private readonly WarningState sampleWarning = new()
    {
        Message = "Sample Warning",
        Priority = 100,
        IconId = 786,
        IconLabel = "Sample Action",
        SourceObjectId = 0xE0000000,
        SourcePlayerName = "Sample Player",
    };
    
    public void Dispose() => Unload();
    
    public void DrawConfig() => DrawableAttribute.DrawAttributes(config, SaveConfig);

    public void Draw(IEnumerable<WarningState> warnings)
    {
        if (!config.Enabled) return;

        if (config.CanDrag || config.SampleMode)
        {
            WarningBanner.Draw(config.WindowPosition, sampleWarning, config);
            DrawDraggableRepositionWindow();
            return;
        }

        var filteredWarnings = config.SoloMode ? warnings.Where(warning => warning.SourceObjectId == Service.ClientState.LocalPlayer?.ObjectId) : warnings;

        switch (config.DisplayMode)
        {
            case BannerOverlayDisplayMode.TopPriority:
                DrawTopPriorityWarnings(filteredWarnings);
                break;

            case BannerOverlayDisplayMode.List:
                DrawListWarnings(filteredWarnings);
                break;
        }
    }

    private void DrawListWarnings(IEnumerable<WarningState> warnings)
    {
        var orderedWarnings = warnings
            .OrderByDescending(warning => warning.Priority)
            .Take(config.WarningCount);

        var warningOffset = new Vector2(0.0f, 95.0f + config.AdditionalSpacing) * config.Scale;
        var position = config.WindowPosition;

        foreach (var warning in orderedWarnings)
        {
            WarningBanner.Draw(position, warning, config);
            position += warningOffset;
        }
    }
    
    private void DrawTopPriorityWarnings(IEnumerable<WarningState> warnings)
    {
        var highestWarning = warnings.MaxBy(warning => warning.Priority);
        
        WarningBanner.Draw(config.WindowPosition, highestWarning, config);
    }
    
    private void DrawDraggableRepositionWindow()
    {
        var sampleWarningSize = new Vector2(545.0f, 110.0f) * config.Scale;
        var infoTextOffset = new Vector2(6.0f, -30.0f) * config.Scale;

        if (config.SampleMode)
        {
            DrawUtilities.TextOutlined(config.WindowPosition + infoTextOffset, "Open NoTankYou Settings to Configure Warnings", 0.5f * config.Scale, KnownColor.White);
        }

        if (config.CanDrag)
        {
            ImGui.SetNextWindowPos(config.WindowPosition);
            ImGui.SetNextWindowSize(sampleWarningSize);
            ImGuiHelpers.ForceNextWindowMainViewport();
            if (ImGui.Begin("##NoTankYouDraggableFrame", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoBackground))
            {
                ImGui.GetBackgroundDrawList().AddRect(config.WindowPosition, config.WindowPosition + sampleWarningSize, ImGui.GetColorU32(new Vector4(1.0f, 0.0f, 0.0f, 1.0f)), 0.0f, ImDrawFlags.RoundCornersNone, 2.0f);

                var pos = ImGui.GetMousePos();
                if (ImGui.IsMouseDown(ImGuiMouseButton.Left) && ImGui.IsWindowFocused())
                {
                    holdOffset ??= config.WindowPosition - pos;
                
                    var old = config.WindowPosition;
                    config.WindowPosition = (Vector2) (pos + holdOffset)!;
                
                    if (old != config.WindowPosition) SaveConfig();
                }
                else
                {
                    holdOffset = null;
                }
            }
            ImGui.End();
        }
    }

    public void Load() => config = LoadConfig();
    public void Unload() { }
    private BannerConfig LoadConfig() => FileController.LoadFile<BannerConfig>("BannerDisplay.config.json", config);
    public void SaveConfig() => FileController.SaveFile("BannerDisplay.config.json", config.GetType(), config);
}