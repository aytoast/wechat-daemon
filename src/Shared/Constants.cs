using System;
using System.Windows.Media;

namespace WeChatSidekick
{
    /// <summary>
    /// centralized application constants.
    /// </summary>
    public static class Constants
    {
        // timing
        public const int TimerIntervalMs = 15;
        public const int ChatSwitchCooldownMs = 300;

        // window dimensions
        public const double SidekickWidth = 360;
        public const double SidekickHeight = 650;

        // fonts
        public static readonly FontFamily PhosphorFont = new FontFamily(new Uri("file:///" + AppDomain.CurrentDomain.BaseDirectory.Replace('\\', '/') + "Assets/"), "./#Phosphor");

        // scroll detection
        public const double ScrollBottomThreshold = 95.0;

        // green bubble detection
        public const int GreenRunThreshold = 8;
        public const int BubbleScanStartOffset = 200;
        public const int BubbleScanEndOffset = 65;
        public const int GreenChannelMinDelta = 30;
        public const int GreenChannelMin = 100;
        public const int BlueChannelMin = 60;

        // message prefixes
        public const string SenderPrefixMe = "[me]: ";
        public const string SenderPrefixOther = "[other]: ";
        public const string SenderPrefixSystem = "[system]: ";
        public const string IslandBoundary = "[ISLAND_BOUNDARY]";

        // wechat automation ids
        public const string AutoIdChatMessageList = "chat_message_list";
        public const string AutoIdSessionList = "session_list";
        public const string AutoIdSessionItemPrefix = "session_item_";
        public const string AutoIdChatInputField = "chat_input_field";
        public const string AutoIdChatNameLabel = "content_view.top_content_view.title_h_view.left_v_view.left_content_v_view.left_ui_.big_title_line_h_view.current_chat_name_label";
        public const string AutoIdNicknameText = "right_v_view.nickname_button_view.display_name_text";
        public const string AutoIdBasicLineView = "right_v_view.user_info_center_view.basic_line_view";
        public const string AutoIdBasicLineKey = "right_v_view.user_info_center_view.basic_line_view.basic_line.key_text";
        public const string AutoIdBasicLineValue = "right_v_view.user_info_center_view.basic_line_view.ProfileTextView";

        // config
        public const string ConfigFile = "sidekick_config.txt";
    }

    /// <summary>
    /// theme colors used throughout the UI.
    /// </summary>
    public static class Theme
    {
        public static readonly Color Background = Color.FromRgb(0x1E, 0x1E, 0x1F);
        public static readonly Color Panel = Color.FromRgb(0x20, 0x20, 0x21);
        public static readonly Color TitleBar = Color.FromRgb(0x25, 0x25, 0x26);
        public static readonly Color Border = Color.FromRgb(0x2A, 0x2A, 0x2B);
        public static readonly Color InputBackground = Color.FromRgb(0x22, 0x22, 0x23);
        public static readonly Color InputBorder = Color.FromRgb(0x3A, 0x3A, 0x3B);
        public static readonly Color ButtonBackground = Color.FromRgb(0x2A, 0x2A, 0x2B);
        public static readonly Color ButtonHover = Color.FromRgb(0x35, 0x35, 0x36);
        public static readonly Color BubbleMe = Color.FromRgb(0x07, 0xC1, 0x60);
        public static readonly Color BubbleOther = Color.FromRgb(0x2F, 0x2F, 0x30);
        public static readonly Color WeChatGreen = Color.FromRgb(0x07, 0xC1, 0x60);
        public static readonly Color WeChatGreenHover = Color.FromRgb(0x15, 0xD0, 0x6A);
        public static readonly Color AccentGreen = Color.FromRgb(0x07, 0xC1, 0x60);
        public static readonly Color AccentGreenMuted = Color.FromRgb(0x12, 0x3A, 0x2A);
        public static readonly Color TextMuted = Color.FromRgb(0x67, 0x67, 0x67);
        public static readonly Color TextDimmed = Color.FromRgb(0x8A, 0x8A, 0x8A);
        public static readonly Color TextSubtle = Color.FromRgb(0xA0, 0xA0, 0xA0);
        public static readonly Color TextRect = Color.FromRgb(110, 110, 110);
        public static readonly Color BadgeSystem = Color.FromRgb(40, 75, 110);
        public static readonly Color BadgeTimestamp = Color.FromRgb(70, 70, 70);
        public static readonly Color BadgeOutOfViewport = Color.FromRgb(150, 100, 20);
        public static readonly Color BadgeDefault = Color.FromRgb(60, 60, 60);
        public static readonly Color BadgeOther = Color.FromRgb(55, 55, 55);
        public static readonly Color SearchHighlight = Color.FromRgb(255, 215, 0);
    }
}
