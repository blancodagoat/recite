using System.Diagnostics;

namespace Recite;

/// <summary>The tray icon is the whole UI: grab, rebind, updates, startup, exit.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly AppConfig config;
    private readonly HotkeyWindow hotkeys;
    private readonly SystemTheme theme;
    private readonly NotifyIcon tray;

    private ToolStripMenuItem? grabItem;
    private HotkeyDialog? hotkeyDialog;
    private bool grabbing;

    public TrayContext(SingleInstance instance)
    {
        config = AppConfig.Load();
        StartupRegistry.Repair();
        if (config.FirstRun)
        {
            StartupRegistry.TrySet(true);
        }

        hotkeys = new HotkeyWindow();
        hotkeys.HotkeyPressed += _ => Grab();
        hotkeys.ShowSettingsRequested += () =>
            Balloon(AppInfo.Name + " is already running", "Right-click the tray icon.", ToolTipIcon.Info);
        instance.ListenForSignals(hotkeys.Handle);

        bool hotkeyOk = hotkeys.Register(HotkeyId.Grab, config.GrabHotkey);

        theme = new SystemTheme();
        theme.Changed += () => tray!.Icon = TrayIcons.ForTaskbar(theme.LightTaskbar);

        tray = new NotifyIcon
        {
            Icon = TrayIcons.ForTaskbar(theme.LightTaskbar),
            Text = AppInfo.Name,
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        tray.DoubleClick += (_, _) => Grab();

        if (!hotkeyOk)
        {
            Balloon(
                "Hotkey unavailable",
                $"{config.GrabHotkey} is taken by another app. Use \"Change hotkey\" in the tray menu.",
                ToolTipIcon.Warning);
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer() };

        grabItem = new ToolStripMenuItem("Grab text", null, (_, _) => Grab())
        {
            ShortcutKeyDisplayString = config.GrabHotkey.ToString(),
            Font = Theme.Ui(9f, FontStyle.Bold),
        };
        menu.Items.Add(grabItem);
        menu.Items.Add(new ToolStripMenuItem("Change hotkey…", null, (_, _) => ShowHotkeyDialog()));
        menu.Items.Add(new ToolStripSeparator());

        var updates = new ToolStripMenuItem("Check for updates");
        updates.Click += async (_, _) =>
        {
            updates.Enabled = false;
            try
            {
                var newer = await UpdateCheck.FindNewer(UpdateCheck.Current);
                if (newer is { } found)
                {
                    Balloon("Update available", $"Recite v{found.Version} is out; opening the release page.", ToolTipIcon.Info);
                    Process.Start(new ProcessStartInfo(found.Url) { UseShellExecute = true });
                }
                else
                {
                    Balloon("Up to date", $"You're on the newest release (v{UpdateCheck.Current}).", ToolTipIcon.None);
                }
            }
            catch
            {
                Balloon("Update check failed", "Couldn't reach GitHub. Try again later.", ToolTipIcon.Warning);
            }
            finally
            {
                updates.Enabled = true;
            }
        };
        menu.Items.Add(updates);

        var startup = new ToolStripMenuItem("Start with Windows");
        startup.Click += (_, _) => { StartupRegistry.TrySet(!StartupRegistry.IsEnabled()); };
        menu.Opening += (_, _) => startup.Checked = StartupRegistry.IsEnabled();
        menu.Items.Add(startup);

        // Hotkeys are not delivered while an elevated window has focus; running elevated
        // ourselves is the only bypass Windows allows.
        if (!Elevation.IsElevated)
        {
            menu.Items.Add(new ToolStripMenuItem("Restart as administrator", null, (_, _) =>
            {
                if (Elevation.TryRestartElevated())
                {
                    ExitThread();
                }
            })
            {
                ToolTipText = "Needed for the hotkey to work while an admin app or game has focus.",
            });
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));
        return menu;
    }

    /// <summary>
    /// The whole point, in one flow: freeze the desktop, let the user pick a region or
    /// click a window, read the text, put it on the clipboard.
    /// </summary>
    private async void Grab()
    {
        if (grabbing)
        {
            return;
        }

        grabbing = true;
        try
        {
            using var desktop = ScreenCapture.CaptureVirtualDesktop();
            using var overlay = new RegionOverlay(desktop);
            if (overlay.ShowDialog() != DialogResult.OK)
            {
                return;
            }

            using var crop = overlay.CreateResult();
            string text = await Ocr.Read(crop);
            if (text.Length == 0)
            {
                Balloon("No text found", "Nothing readable in that region.", ToolTipIcon.None);
                return;
            }

            Clipboard.SetText(text);
            Balloon("Copied", Preview(text), ToolTipIcon.None);
            AppLog.Write($"grabbed {text.Length} chars");
        }
        catch (Exception ex)
        {
            AppLog.Write("grab failed: " + ex.Message);
            Balloon("Couldn't read text", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            grabbing = false;
        }
    }

    private static string Preview(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= 60 ? line : line[..60] + "…";
    }

    private void ShowHotkeyDialog()
    {
        if (hotkeyDialog is { IsDisposed: false })
        {
            hotkeyDialog.Activate();
            return;
        }

        hotkeyDialog = new HotkeyDialog(
            config.GrabHotkey,
            candidate =>
            {
                // Swap live: register the candidate; on refusal put the old one back so
                // the app is never left without a working hotkey.
                hotkeys.Unregister(HotkeyId.Grab);
                if (hotkeys.Register(HotkeyId.Grab, candidate))
                {
                    config.GrabHotkey = candidate;
                    config.Save();
                    if (grabItem is not null)
                    {
                        grabItem.ShortcutKeyDisplayString = candidate.ToString();
                    }

                    return true;
                }

                hotkeys.Register(HotkeyId.Grab, config.GrabHotkey);
                return false;
            },
            recording =>
            {
                if (recording)
                {
                    hotkeys.Unregister(HotkeyId.Grab);
                }
                else
                {
                    hotkeys.Register(HotkeyId.Grab, config.GrabHotkey);
                }
            });
        hotkeyDialog.Show();
    }

    private void Balloon(string title, string text, ToolTipIcon icon) =>
        tray.ShowBalloonTip(4000, title, text, icon);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tray.Visible = false;
            tray.Dispose();
            hotkeys.Dispose();
            theme.Dispose();
        }

        base.Dispose(disposing);
    }
}
