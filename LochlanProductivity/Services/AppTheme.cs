using System;
using System.Collections.Generic;

namespace LochlanProductivity.Services
{
    // ============================================================
    // APP THEME PALETTES
    //
    // Every color the main surface uses lives here so themes are
    // one object: Frost (light, misty field) and Tapestry (dark
    // millefleurs: botanical near-black, ruby, gold, parchment).
    // User group-dot colors are separate (AppGroup.Color) and work
    // on both. To add a theme: add a static palette + list it in
    // All. Machine-local pref (AppSettingsManager.ThemeName), not
    // synced.
    // ============================================================

    public class AppThemePalette
    {
        public string Name { get; init; } = "Frost";

        public string DisplayName { get; init; } = "Frost";

        public string Description { get; init; } = "";

        public bool IsDark { get; init; }

        public Windows.UI.Color WindowBackground { get; init; }

        public Windows.UI.Color CardBackground { get; init; }

        public Windows.UI.Color CardBorder { get; init; }

        public Windows.UI.Color IncompleteCardBorder { get; init; }

        public Windows.UI.Color NoteCardBackground { get; init; }

        public Windows.UI.Color NoteCardBorder { get; init; }

        public Windows.UI.Color InkText { get; init; }

        public Windows.UI.Color MutedText { get; init; }

        public Windows.UI.Color PrimaryButton { get; init; }

        public Windows.UI.Color PrimaryButtonText { get; init; }

        public Windows.UI.Color SecondaryButton { get; init; }

        public Windows.UI.Color SecondaryButtonText { get; init; }

        public Windows.UI.Color GhostButton { get; init; }

        public Windows.UI.Color GhostButtonText { get; init; }

        public Windows.UI.Color GhostButtonBorder { get; init; }

        public Windows.UI.Color DangerButton { get; init; }

        public Windows.UI.Color DangerButtonText { get; init; }

        public Windows.UI.Color DangerButtonBorder { get; init; }

        public Windows.UI.Color AccentBar { get; init; }

        public Windows.UI.Color OverdueText { get; init; }

        public Windows.UI.Color NotificationBackground { get; init; }

        public Windows.UI.Color NotificationBorder { get; init; }

        public Windows.UI.Color InputBackground { get; init; }

        public Windows.UI.Color InputBorder { get; init; }

        public Windows.UI.Color SyncDot { get; init; }

        public Windows.UI.Color SyncDotBusy { get; init; }

        public Windows.UI.Color DensityDot { get; init; }

        private static Windows.UI.Color C(byte a, byte r, byte g, byte b) =>
            Microsoft.UI.ColorHelper.FromArgb(a, r, g, b);

        private static Windows.UI.Color C(string hex)
        {
            string h = hex.Trim().TrimStart('#');

            if (h.Length == 6)
                h = "FF" + h;

            return C(
                Convert.ToByte(h.Substring(0, 2), 16),
                Convert.ToByte(h.Substring(2, 2), 16),
                Convert.ToByte(h.Substring(4, 2), 16),
                Convert.ToByte(h.Substring(6, 2), 16));
        }

        public static AppThemePalette Frost { get; } = new()
        {
            Name = "Frost",
            DisplayName = "Frost",
            Description = "Light and airy — misty field.",
            IsDark = false,
            WindowBackground = C(153, 242, 243, 240),
            CardBackground = C(204, 250, 251, 249),
            CardBorder = C("#DDE3E0"),
            IncompleteCardBorder = C("#8E7D6B"),
            NoteCardBackground = C(204, 242, 243, 240),
            NoteCardBorder = C("#D2DCD2"),
            InkText = C("#3A2E28"),
            MutedText = C("#6B5E52"),
            PrimaryButton = C("#8A9A8B"),
            PrimaryButtonText = Microsoft.UI.Colors.White,
            SecondaryButton = C("#E8ECE8"),
            SecondaryButtonText = C("#2E3440"),
            GhostButton = C("#F2F3F0"),
            GhostButtonText = C("#5A6460"),
            GhostButtonBorder = C("#DDE3E0"),
            DangerButton = C("#FFF8F0"),
            DangerButtonText = C("#A65D3C"),
            DangerButtonBorder = C("#E8CFCF"),
            AccentBar = C("#8E7D6B"),
            OverdueText = Microsoft.UI.Colors.OrangeRed,
            NotificationBackground = C("#F8F0F0"),
            NotificationBorder = C("#E8CFCF"),
            InputBackground = Microsoft.UI.Colors.White,
            InputBorder = C("#E8ECE8"),
            SyncDot = C("#8A9A8B"),
            SyncDotBusy = C("#C4B8AC"),
            DensityDot = C("#8A9A8B")
        };

        public static AppThemePalette Tapestry { get; } = new()
        {
            Name = "Tapestry",
            DisplayName = "Tapestry",
            Description = "Dark millefleurs — ruby, gold, parchment.",
            IsDark = true,
            WindowBackground = C(242, 22, 26, 23),
            CardBackground = C(242, 36, 44, 40),
            CardBorder = C("#4E4433"),
            IncompleteCardBorder = C("#C09A3E"),
            NoteCardBackground = C(242, 38, 46, 44),
            NoteCardBorder = C("#55644F"),
            InkText = C("#F0E4CC"),
            MutedText = C("#B3A586"),
            PrimaryButton = C("#8E2F3C"),
            PrimaryButtonText = C("#F7EDD8"),
            SecondaryButton = C("#3A443C"),
            SecondaryButtonText = C("#EAD9B8"),
            GhostButton = C("#2A332E"),
            GhostButtonText = C("#C9BBA0"),
            GhostButtonBorder = C("#4E4433"),
            DangerButton = C("#40211C"),
            DangerButtonText = C("#E08A7E"),
            DangerButtonBorder = C("#7A4438"),
            AccentBar = C("#C09A3E"),
            OverdueText = C("#F0703F"),
            NotificationBackground = C("#3A2320"),
            NotificationBorder = C("#7A4438"),
            InputBackground = C("#1E2422"),
            InputBorder = C("#55644F"),
            SyncDot = C("#9DB18A"),
            SyncDotBusy = C("#8E7D6B"),
            DensityDot = C("#C09A3E")
        };

        public static IReadOnlyList<AppThemePalette> All { get; } =
            new[] { Frost, Tapestry };

        public static AppThemePalette FromName(string? name) =>
            string.Equals(name, Tapestry.Name, StringComparison.OrdinalIgnoreCase)
                ? Tapestry
                : Frost;
    }
}
