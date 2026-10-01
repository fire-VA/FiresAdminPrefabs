using System;
using UnityEngine;

using FiresCore.Net;

namespace VerdantsAscent.Sync
{
    // Post-sync banner offering "Reconnect Now" or "Continue Anyway". Drawn with
    // IMGUI so it needs no prefab, no Canvas, and none of the Hud-parenting rules.
    public static class VAFapSyncReconnectPrompt
    {
        private static GameObject _hostGo;
        private static PromptHost _host;

        public static void Show(int filesReceived, long bytesReceived)
        {
            if (_host == null || _hostGo == null)
            {
                _hostGo = new GameObject("VAFapSyncReconnectPrompt");
                UnityEngine.Object.DontDestroyOnLoad(_hostGo);
                _host = _hostGo.AddComponent<PromptHost>();
            }
            _host.FilesReceived = filesReceived;
            _host.BytesReceived = bytesReceived;
            _host.Visible = true;
        }

        public static void Hide()
        {
            if (_host != null) _host.Visible = false;
            if (_hostGo != null)
            {
                FiresCore.Net.NetworkObjectHelper.SafeDestroy(_hostGo);
                _hostGo = null;
                _host = null;
            }
        }

        private class PromptHost : MonoBehaviour
        {
            private const int BoxWidth = 540;
            private const int BoxHeight = 200;
            private const int BoxPaddingX = 20;
            private const int BoxPaddingY = 18;
            private const int HeaderHeight = 28;
            private const int BodyOffsetY = 36;
            private const int BodyHeight = 60;
            private const int ButtonWidth = 200;
            private const int ButtonHeight = 34;
            private const int ButtonGap = 16;
            private const int ButtonRowBottomInset = 50;
            private const int HeaderFontSize = 18;
            private const int BodyFontSize = 14;

            private static readonly Color HeaderColor = new Color(1f, 0.92f, 0.65f, 1f);
            private static readonly Color BodyColor = new Color(0.92f, 0.92f, 0.92f, 1f);
            private static readonly Color BackdropColor = new Color(0.06f, 0.06f, 0.08f, 0.92f);

            public bool Visible;
            public int FilesReceived;
            public long BytesReceived;

            private GUIStyle _headerStyle;
            private GUIStyle _bodyStyle;
            private GUIStyle _buttonStyle;
            private Texture2D _backdrop;

            private void OnDestroy()
            {
                if (_backdrop != null) Destroy(_backdrop);
            }

            // GUI.skin is only valid inside OnGUI, and the default styles allocate
            // per call, so they are built once on first draw.
            private void EnsureStyles()
            {
                if (_headerStyle == null)
                {
                    _headerStyle = new GUIStyle(GUI.skin.label)
                    {
                        fontSize = HeaderFontSize,
                        fontStyle = FontStyle.Bold,
                        alignment = TextAnchor.MiddleCenter,
                        normal = { textColor = HeaderColor },
                    };
                }
                if (_bodyStyle == null)
                {
                    _bodyStyle = new GUIStyle(GUI.skin.label)
                    {
                        fontSize = BodyFontSize,
                        alignment = TextAnchor.MiddleCenter,
                        wordWrap = true,
                        normal = { textColor = BodyColor },
                    };
                }
                if (_buttonStyle == null)
                {
                    _buttonStyle = new GUIStyle(GUI.skin.button)
                    {
                        fontSize = BodyFontSize,
                        fontStyle = FontStyle.Bold,
                    };
                }
                if (_backdrop == null)
                {
                    _backdrop = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                    _backdrop.SetPixel(0, 0, BackdropColor);
                    _backdrop.Apply();
                }
            }

            private void OnGUI()
            {
                if (!Visible) return;

                EnsureStyles();

                int x = (Screen.width - BoxWidth) / 2;
                int y = (Screen.height - BoxHeight) / 2;

                GUI.DrawTexture(new Rect(x, y, BoxWidth, BoxHeight), _backdrop);
                GUI.Box(new Rect(x, y, BoxWidth, BoxHeight), GUIContent.none);

                int innerX = x + BoxPaddingX;
                int innerY = y + BoxPaddingY;
                int innerWidth = BoxWidth - BoxPaddingX * 2;

                GUI.Label(new Rect(innerX, innerY, innerWidth, HeaderHeight),
                    "FiresAdminPrefabs — server assets updated",
                    _headerStyle);

                GUI.Label(new Rect(innerX, innerY + BodyOffsetY, innerWidth, BodyHeight),
                    $"Synced {FilesReceived} file(s) ({VAFapAssetSync.FormatBytes(BytesReceived)}) from the server.\n"
                    + "Reconnect to load the new assets — the hammer and prefab catalog "
                    + "won't pick them up until then.",
                    _bodyStyle);

                int buttonY = y + BoxHeight - ButtonRowBottomInset;
                int buttonRowWidth = ButtonWidth * 2 + ButtonGap;
                int buttonsX = x + (BoxWidth - buttonRowWidth) / 2;

                if (GUI.Button(new Rect(buttonsX, buttonY, ButtonWidth, ButtonHeight), "Reconnect Now", _buttonStyle))
                {
                    DisconnectToMenu();
                    Visible = false;
                }

                if (GUI.Button(new Rect(buttonsX + ButtonWidth + ButtonGap, buttonY, ButtonWidth, ButtonHeight),
                    "Continue Anyway", _buttonStyle))
                {
                    Visible = false;
                    Debug.Log("[FAP Sync] User chose 'Continue Anyway' — playing with stale bundles "
                        + "until next reconnect.");
                }
            }

            // Game.Logout runs the world/character save before dropping the connection;
            // ZNet.Shutdown is only the fallback when there is no Game instance.
            private static void DisconnectToMenu()
            {
                try
                {
                    if (Game.instance != null)
                    {
                        Game.instance.Logout();
                        Debug.Log("[FAP Sync] Game.Logout() invoked; player returning to start menu.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[FAP Sync] Game.Logout failed: {ex.Message}");
                }

                try
                {
                    if (ZNet.instance != null) ZNet.instance.Shutdown();
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[FAP Sync] ZNet.Shutdown failed: {ex.Message}");
                }
            }
        }
    }
}
