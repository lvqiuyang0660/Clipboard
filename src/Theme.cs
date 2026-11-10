using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace ClipHelper
{
    /// <summary>视觉变量：与 UI 稿（深色默认 / 浅色可切换）保持一致。</summary>
    public static class Theme
    {
        public static bool IsLight;

        public static Brush Txt;
        public static Brush Txt2;
        public static Brush Txt3;
        public static Brush Acc;
        public static Brush AccFg;
        public static Brush AccSoft;
        public static Brush AccSofter;
        public static Brush DangerBrush;
        public static Brush Panel;
        public static Brush PanelSolid;
        public static Brush PanelBorder;
        public static Brush Line;
        public static Brush Card;
        public static Brush CardBorder;
        public static Brush Hover;
        public static Brush Active;
        public static Brush Field;
        public static Brush FieldBorder;
        public static Brush Chip;
        public static Brush ChipBorder;
        public static Brush Kbd;
        public static Brush Btn;
        public static Brush SwitchOff;
        public static Brush SegBrush;
        public static Brush SegActive;
        public static Brush Menu;
        public static Brush OnAccentKnob;

        public static Color AccColor;

        public static FontFamily Font;
        public static FontFamily Mono;

        public static readonly string[] AppPalette = new string[]
        {
            "#3370FF", "#3AA0D8", "#4A9EE8", "#F2B25C", "#3FBE6F",
            "#A259FF", "#2B7CD3", "#D6A13A", "#2F8DE4", "#7C8794"
        };

        static Theme()
        {
            Font = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI");
            Mono = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI");
            Apply(false);
        }

        public static void Apply(bool light)
        {
            IsLight = light;
            if (light)
            {
                Txt = Hex("#14171C");
                Txt2 = Hex("#565C66");
                Txt3 = Hex("#767D87");
                Acc = Hex("#0F6CBD");
                AccColor = Color.FromRgb(0x0F, 0x6C, 0xBD);
                AccFg = Hex("#FFFFFF");
                AccSoft = Argba(0.11, "#0F6CBD");
                AccSofter = Argba(0.045, "#0F6CBD");
                DangerBrush = Hex("#C42B1C");
                Panel = Hex("#F2F5F9");
                PanelSolid = Hex("#FCFCFD");
                PanelBorder = Argba(0.12, "#000000");
                Line = Argba(0.075, "#000000");
                Card = Argba(0.76, "#FFFFFF");
                CardBorder = Argba(0.07, "#000000");
                Hover = Argba(0.04, "#000000");
                Active = Argba(0.075, "#000000");
                Field = Argba(0.92, "#FFFFFF");
                FieldBorder = Argba(0.12, "#000000");
                Chip = Argba(0.80, "#FFFFFF");
                ChipBorder = Argba(0.08, "#000000");
                Kbd = Argba(0.05, "#000000");
                Btn = Argba(0.82, "#FFFFFF");
                SwitchOff = Argba(0.26, "#000000");
                SegBrush = Argba(0.045, "#000000");
                SegActive = Hex("#FFFFFF");
                Menu = Hex("#FCFCFD");
                OnAccentKnob = Hex("#FFFFFF");
            }
            else
            {
                Txt = Hex("#F2F4F7");
                Txt2 = Hex("#A2A8B3");
                Txt3 = Hex("#6F7681");
                Acc = Hex("#60CDFF");
                AccColor = Color.FromRgb(0x60, 0xCD, 0xFF);
                AccFg = Hex("#052B3D");
                AccSoft = Argba(0.13, "#60CDFF");
                AccSofter = Argba(0.07, "#60CDFF");
                DangerBrush = Hex("#FF99A4");
                Panel = Hex("#1B1D21");
                PanelSolid = Hex("#202226");
                PanelBorder = Argba(0.09, "#FFFFFF");
                Line = Argba(0.075, "#FFFFFF");
                Card = Argba(0.035, "#FFFFFF");
                CardBorder = Argba(0.06, "#FFFFFF");
                Hover = Argba(0.055, "#FFFFFF");
                Active = Argba(0.095, "#FFFFFF");
                Field = Argba(0.05, "#FFFFFF");
                FieldBorder = Argba(0.08, "#FFFFFF");
                Chip = Argba(0.05, "#FFFFFF");
                ChipBorder = Argba(0.07, "#FFFFFF");
                Kbd = Argba(0.07, "#FFFFFF");
                Btn = Argba(0.06, "#FFFFFF");
                SwitchOff = Argba(0.22, "#FFFFFF");
                SegBrush = Argba(0.05, "#FFFFFF");
                SegActive = Argba(0.13, "#FFFFFF");
                Menu = Hex("#2C2E32");
                OnAccentKnob = Hex("#052B3D");
            }
        }

        public static SolidColorBrush Hex(string hex)
        {
            SolidColorBrush b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        public static SolidColorBrush Argba(double alpha, string hex)
        {
            Color c = (Color)ColorConverter.ConvertFromString(hex);
            byte a = (byte)Math.Round(alpha * 255);
            SolidColorBrush b = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        public static Brush AppColorBrush(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return Txt3;
            try
            {
                SolidColorBrush b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                b.Freeze();
                return b;
            }
            catch { return Txt3; }
        }

        public static DropShadowEffect Shadow(double blur, double opacity)
        {
            DropShadowEffect e = new DropShadowEffect();
            e.BlurRadius = blur;
            e.ShadowDepth = 2;
            e.Direction = 270;
            e.Opacity = opacity;
            e.Color = Colors.Black;
            return e;
        }
    }
}
