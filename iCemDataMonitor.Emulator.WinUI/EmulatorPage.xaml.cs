using System.Globalization;
using System.Reactive.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ReactiveUI;
using Halliburton.iCemEmule.Control;

namespace iCemDataMonitor.Emulator.WinUI;

/// <summary>
/// W11.6/W11.7, merged app 2026-08-27: the emulator page — one reusable
/// UserControl hosted ONLY by iCemEmulator.exe (the shell no longer embeds
/// it). Owns its view-model, the SHARED pipe client and tab rendering. NO
/// copy-paste of the old WinForms tabs: this IS the new UI, shared by
/// construction. The engine runs IN-PROCESS inside the app — there is no
/// launch stub, no process supervision, no host RPC here.
/// macOS design tokens (MacAccentBrush, ...) ship inside THIS library and are
/// merged at page level (self-sufficient, no host theme contract).
/// </summary>
public sealed partial class EmulatorPage : UserControl
{
    /// <summary>ComboBox item for a LAN signal edit target (sv + name).</summary>
    private sealed record LanEditItem(string Sv, string Name)
    {
        public override string ToString() => $"{Name}  ({Sv})";
    }

    /// <summary>ComboBox item for a unit group (engine key + russian label).</summary>
    private sealed record UnitGroupItem(string Key, string Label)
    {
        public override string ToString() => Label;
    }

    private static readonly string[] PresetItems =
    {
        "0 — FirmwareDefault (прошивка: gpm, °C, ppg, psi)",
        "1 — LanNative (провод: bbl/min, °F, lb/gal)",
        "2 — Metric (m³/min, °C, g/cc, bar)",
        "3 — Imperial (bbl/min, °F, ppg, psi)"
    };

    private static readonly UnitGroupItem[] UnitGroups =
    {
        new("flow", "Расход (flow)"),
        new("temperature", "Температура"),
        new("density", "Плотность"),
        new("volume", "Объём"),
        new("level", "Уровень"),
        new("pressure", "Давление")
    };

    private readonly EmulatorViewModel _vm = new();
    private bool _syncingSwitches;    private bool _syncingProfile; // P3: gate for programmatic ProfileCombo sync
    private bool _equipmentFocused;
    // W14: race-gates (снапшот не трогает свитч, пока его команда в полёте)
    private bool _busyCom, _busyLan, _busyFirmware, _busyEmulate;
    // W22: the view-model owns a 1 Hz poll loop — it must not outlive the control.
    private bool _vmDisposed;

    public EmulatorPage()
    {
        // W11.10 (audit A3): the control is SELF-SUFFICIENT — macOS tokens
        // ship inside THIS library (ms-appx resource), merged at page level.
        // Any host works without a theme contract; page brushes resolve from
        // the page's own resources (never Application.Current.Resources).
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("ms-appx:///iCemDataMonitor.Emulator.WinUI/ThemeResources.xaml"),
        });

        // W15: hard-wire the UI scheduler — the VM applies INPC row updates
        // through THIS page's DispatcherQueue (RxApp.MainThreadScheduler is NOT
        // UI-bound in a raw WinUI standalone: 0x8001010E, crash.log 2026-08-29).
        _vm.BindUi(DispatcherQueue);

        InitializeComponent();

        // W22 (dispose law): Unloaded here is terminal for this control (it is
        // hosted once per window by iCemEmule.WinUI) — stop the VM's 1 Hz
        // pipe/process polling instead of leaking it past teardown.
        Unloaded += (_, _) => DisposeViewModel();

        // W13: equal-width segments (kit EqualSegmentsPanel doctrine) — the
        // track stretches to full width; every item gets trackWidth/count.
        void SyncTabWidthsImpl()
        {
            var w = EmuTabs.ActualWidth;
            if (w <= 0) return;
            foreach (var item in EmuTabs.Items)
                item.Width = w / EmuTabs.Items.Count;
        }
        // W13.6 race fix: через SizeChanged ActualWidth трека был STALE
        // (parent шире рамки child в том же pass — autopsy 2026-08-20: попытка
        // sync давала 573 вместо 747, табы сидели узкими на первом кадре после Load).
        // Low-priority enqueue = callback после всего layout-pass ⇒ ActualWidth финальный.
        Microsoft.UI.Dispatching.DispatcherQueueHandler SyncTabWidths = SyncTabWidthsImpl;
        void ScheduleTabSync() =>
            EmuTabs.DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, SyncTabWidths);
        EmuTabsTrack.SizeChanged += (_, _) => ScheduleTabSync();
        EmuTabs.SizeChanged += (_, _) => ScheduleTabSync();
        EmuTabs.Loaded += (_, _) => ScheduleTabSync();

        var ctx = SynchronizationContext.Current!;

        // P4 2026-08-27: the command note is the toggles' ONLY feedback —
        // render ⚠ refusals in the MacOrange warning token, successes in the
        // secondary label color (re-resolved on theme change).
        void ApplyNoteBrush() =>
            CommandNoteText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Resources[
                CommandNoteText.Text.StartsWith("⚠", StringComparison.Ordinal)
                    ? "MacOrangeBrush" : "MacLabel2Brush"];
        ActualThemeChanged += (_, _) => ApplyNoteBrush();

            // W14 race-gate (унаследован W11.1 по хлебцу): если toggle-команда в полёте,
            // 1с snapshot не может врать перевёрнутый свитч (иначе свитч
            // «прыгнет» между фактами: клик ON → stale snapshot OFF → прилетит ON).
        _vm.ToggleComCommand.IsExecuting.ObserveOn(ctx).Subscribe(b => _busyCom = b);
        _vm.ToggleLanCommand.IsExecuting.ObserveOn(ctx).Subscribe(b => _busyLan = b);
        _vm.SetFirmwareCommand.IsExecuting.ObserveOn(ctx).Subscribe(b => _busyFirmware = b);
        _vm.SetEmulateCommand.IsExecuting.ObserveOn(ctx).Subscribe(b => _busyEmulate = b);
        EmuStatusText.Text = _vm.StatusText;
        _vm.WhenAnyValue(x => x.Snapshot).ObserveOn(ctx).Subscribe(RenderSnapshot);
        // P3 2026-08-27: programmatic selection restore (refresh/startup) must
        // NOT echo a `profile` command back to the engine — the flag gates the
        // SelectionChanged handler while ItemsSource/SelectedItem are synced.
        _vm.Profiles.ObserveOn(ctx).Subscribe(p =>
        {
            _syncingProfile = true;
            ProfileCombo.ItemsSource = p.Select(x => x.Name).ToList();
            var active = p.FirstOrDefault(x => x.Active);
            if (active is not null) ProfileCombo.SelectedItem = active.Name;
            _syncingProfile = false;
        });
        _vm.ComVars.ObserveOn(ctx).Subscribe(v =>
        {
            // W15: the VM emits the SAME list instance with INPC rows updated
            // in place (native digit updates). Re-set ItemsSource only when
            // the instance actually changed (template swap / first fill) —
            // no rebuild ⇒ no flicker (owner decree: «обновление должно быть
            // нативным изменением цифры»).
            if (ReferenceEquals(ComVarsList.ItemsSource, v)) return;

            // W14: refresh НЕ сбрасывает выбор (жалоба: «Установить» → список
            // пересоздан → выбранная переменная слетала). Сохраняем по имени.
            var selVar = ComVarCombo.SelectedItem as string;
            var selRow = (ComVarsList.SelectedItem as EmuComVar)?.Name;
            ComVarsList.ItemsSource = v;
            var names = v.Select(x => x.Name).ToList();
            ComVarCombo.ItemsSource = names;
            if (selVar is not null && names.Contains(selVar))
                ComVarCombo.SelectedItem = selVar;
            if (selRow is not null)
            {
                var row = v.FirstOrDefault(x => x.Name == selRow);
                if (row is not null) ComVarsList.SelectedItem = row;
            }
        });
        _vm.LanVars.ObserveOn(ctx).Subscribe(v =>
        {
            // W15: same-instance guard — see the ComVars subscription.
            if (ReferenceEquals(LanVarsList.ItemsSource, v)) return;

            var selVar = (LanVarCombo.SelectedItem as LanEditItem)?.Sv;
            var selRow = (LanVarsList.SelectedItem as EmuLanVar)?.Sv;
            LanVarsList.ItemsSource = v;
            var items = v.Select(x => new LanEditItem(x.Sv, x.Name)).ToList();
            LanVarCombo.ItemsSource = items;
            if (selVar is not null)
            {
                var match = items.FirstOrDefault(x => x.Sv == selVar);
                if (match is not null) LanVarCombo.SelectedItem = match;
            }
            if (selRow is not null)
            {
                var row = v.FirstOrDefault(x => x.Sv == selRow);
                if (row is not null) LanVarsList.SelectedItem = row;
            }
        });
        _vm.VarsNote.ObserveOn(ctx).Subscribe(t => VarsNote.Text = t);

        // W11.7: endpoints + command feedback
        _vm.Ports.ObserveOn(ctx).Subscribe(p =>
        {
            ComPortCombo.ItemsSource = p.ToList();
            var current = _vm.Snapshot.ComPort;
            if (current is not ("—" or "") && p.Contains(current))
                ComPortCombo.SelectedItem = current;
        });
        _vm.Adapters.ObserveOn(ctx).Subscribe(a =>
        {
            LanAdapterCombo.ItemsSource = a.ToList();
            var current = _vm.Snapshot.LanAdapter;
            if (current is not ("—" or "") && a.Contains(current))
                LanAdapterCombo.SelectedItem = current;
        });
        // 2026-08-29 (Npcap/adapter honesty): hint line under the combo —
        // visible only while the probed adapter list is empty.
        _vm.LanAdapterHint.ObserveOn(ctx).Subscribe(t =>
        {
            LanAdapterHintText.Text = t;
            LanAdapterHintText.Visibility = string.IsNullOrEmpty(t) ? Visibility.Collapsed : Visibility.Visible;
        });
        _vm.CommandNote.ObserveOn(ctx).Subscribe(t =>
        {
            CommandNoteText.Text = t;
            ApplyNoteBrush();
        });

        // P4 2026-08-27 (lying-switch kill): render the stream switches from
        // the engine's AUTHORITATIVE reply (data.streaming) the moment the
        // toggle command answers — instant ON/OFF on success, instant
        // snap-back OFF on a refused start (the refusal note stays: nothing
        // else writes the command note). The 1 Hz snapshot only confirms.
        // _syncingSwitches guards the snap so the Toggled handler doesn't
        // re-echo a command back at the engine.
        _vm.ToggleTruth.ObserveOn(ctx).Subscribe(t =>
        {
            _syncingSwitches = true;
            if (t.Channel == "com") ComStreamSwitch.IsOn = t.Streaming;
            else if (t.Channel == "lan") LanStreamSwitch.IsOn = t.Streaming;
            _syncingSwitches = false;
        });

        RefreshVarsButton.Click += (_, _) => _vm.RefreshVarsCommand.Execute().Subscribe();
        RefreshPortsButton.Click += (_, _) => _vm.RefreshEndpointsCommand.Execute().Subscribe();
        RefreshAdaptersButton.Click += (_, _) => _vm.RefreshEndpointsCommand.Execute().Subscribe();

        ProfileCombo.SelectionChanged += (_, _) =>
        {
            if (_syncingProfile) return;
            if (ProfileCombo.SelectedItem is string name)
                _vm.SetProfileCommand.Execute(name).Subscribe();
        };
        ComPortCombo.SelectionChanged += (_, _) =>
        {
            if (ComPortCombo.SelectedItem is string port && port != _vm.Snapshot.ComPort)
                _vm.SetPortCommand.Execute(port).Subscribe();
        };
        LanAdapterCombo.SelectionChanged += (_, _) =>
        {
            if (LanAdapterCombo.SelectedItem is string adapter && adapter != _vm.Snapshot.LanAdapter)
                _vm.SetAdapterCommand.Execute(adapter).Subscribe();
        };

        ComStreamSwitch.Toggled += (_, _) =>
        {
            UiTrace.Log($"ComStreamSwitch.Toggled IsOn={ComStreamSwitch.IsOn} syncing={_syncingSwitches} busy={_busyCom}");
            if (_syncingSwitches) return;
            // P3 2026-08-27: pass the switch's own target state — the VM no
            // longer re-reads the (possibly stale) snapshot.
            _vm.ToggleComCommand.Execute(ComStreamSwitch.IsOn).Subscribe();
        };
        LanStreamSwitch.Toggled += (_, _) =>
        {
            UiTrace.Log($"LanStreamSwitch.Toggled IsOn={LanStreamSwitch.IsOn} syncing={_syncingSwitches} busy={_busyLan}");
            if (_syncingSwitches) return;
            _vm.ToggleLanCommand.Execute(LanStreamSwitch.IsOn).Subscribe();
        };
        FirmwareSwitch.Toggled += (_, _) =>
        {
            UiTrace.Log($"FirmwareSwitch.Toggled IsOn={FirmwareSwitch.IsOn} syncing={_syncingSwitches} busy={_busyFirmware}");
            if (_syncingSwitches) return;
            _vm.SetFirmwareCommand.Execute(FirmwareSwitch.IsOn).Subscribe();
        };
        // W14: emulation gates (flecsEmu "Emule" → ToggleSwitch)
        ComEmulateSwitch.Toggled += (_, _) =>
        {
            UiTrace.Log($"ComEmulateSwitch.Toggled IsOn={ComEmulateSwitch.IsOn} syncing={_syncingSwitches} busy={_busyEmulate}");
            if (_syncingSwitches) return;
            _vm.SetEmulateCommand.Execute(("com", ComEmulateSwitch.IsOn)).Subscribe();
        };
        LanEmulateSwitch.Toggled += (_, _) =>
        {
            UiTrace.Log($"LanEmulateSwitch.Toggled IsOn={LanEmulateSwitch.IsOn} syncing={_syncingSwitches} busy={_busyEmulate}");
            if (_syncingSwitches) return;
            _vm.SetEmulateCommand.Execute(("lan", LanEmulateSwitch.IsOn)).Subscribe();
        };

        // W14: строки по обоим таблицам — подсветка (SelectionMode=Single) +
        // переменная загружается в редактор (как в старом flecsEmu)
        ComVarsList.SelectionChanged += (_, _) =>
        {
            if (ComVarsList.SelectedItem is EmuComVar row)
            {
                ComVarCombo.SelectedItem = row.Name;
                if (TryNum(row.Value, out _)) ComValueBox.Text = row.Value;
            }
        };
        LanVarsList.SelectionChanged += (_, _) =>
        {
            if (LanVarsList.SelectedItem is EmuLanVar row)
            {
                LanVarCombo.SelectedItem = new LanEditItem(row.Sv, row.Name);
                if (TryNum(row.Value, out _)) LanValueBox.Text = row.Value;
            }
        };

        EquipmentBox.GotFocus += (_, _) => _equipmentFocused = true;
        EquipmentBox.LostFocus += (_, _) => _equipmentFocused = false;
        ApplyEquipmentButton.Click += (_, _) =>
            _vm.SetEquipmentCommand.Execute(EquipmentBox.Text).Subscribe();

        PresetCombo.SelectionChanged += (_, _) =>
        {
            if (_syncingSwitches) return;
            int index = PresetCombo.SelectedIndex;
            if (index >= 0 && index != _vm.Snapshot.UnitsPresetIndex)
                _vm.SetPresetCommand.Execute(index).Subscribe();
        };

        UnitApplyButton.Click += (_, _) =>
        {
            if (UnitGroupCombo.SelectedItem is UnitGroupItem g && !string.IsNullOrWhiteSpace(UnitValueBox.Text))
                _vm.SetUnitCommand.Execute((g.Key, UnitValueBox.Text)).Subscribe();
            else
            CommandNoteText.Text = "⚠ выберите группу в списке слева";
        };
        UnitClearButton.Click += (_, _) =>
        {
            if (UnitGroupCombo.SelectedItem is UnitGroupItem g)
            {
                UnitValueBox.Text = "";
                _vm.SetUnitCommand.Execute((g.Key, "")).Subscribe();
            }
            else
            CommandNoteText.Text = "⚠ выберите группу или «по пресету»";
        };

        ComSetButton.Click += (_, _) =>
        {
            if (ComVarCombo.SelectedItem is string name && TryNum(ComValueBox.Text, out double v))
                _vm.SetVarCommand.Execute(new EmuVarEdit("com", null, name, v)).Subscribe();
            else
            CommandNoteText.Text = "⚠ выберите переменную в списке слева";
        };
        ComClearButton.Click += (_, _) =>
        {
            if (ComVarCombo.SelectedItem is string name)
                _vm.ClearVarCommand.Execute(new EmuVarEdit("com", null, name, 0)).Subscribe();
            else
            CommandNoteText.Text = "⚠ выберите переменную или «все»";
        };
        LanSetButton.Click += (_, _) =>
        {
            if (LanVarCombo.SelectedItem is LanEditItem item && TryNum(LanValueBox.Text, out double v))
                _vm.SetVarCommand.Execute(new EmuVarEdit("lan", item.Sv, item.Name, v)).Subscribe();
            else
            CommandNoteText.Text = "⚠ выберите сигнал в списке слева";
        };
        LanClearButton.Click += (_, _) =>
        {
            if (LanVarCombo.SelectedItem is LanEditItem item)
                _vm.ClearVarCommand.Execute(new EmuVarEdit("lan", item.Sv, item.Name, 0)).Subscribe();
            else
            CommandNoteText.Text = "⚠ выберите сигнал или «все»";
        };

        EmuTabs.SelectionChanged += (_, _) =>
        {
            var text = (EmuTabs.SelectedItem as SelectorBarItem)?.Text ?? "COM";
            ComPanel.Visibility = text == "COM" ? Visibility.Visible : Visibility.Collapsed;
            LanPanel.Visibility = text == "LAN" ? Visibility.Visible : Visibility.Collapsed;
            UnitsPanel.Visibility = text == "Units" ? Visibility.Visible : Visibility.Collapsed;
        };

        // W15 diagnostic: ctor completion — every subscription above attached.
        UiTrace.Log($"EmulatorPage ctor ok; executorSet={ControlPipeClient.LocalExecutor is not null}");

        // W15 diagnostic: pointer probes — WHERE does the click actually land?
        // The toggles produced ZERO Toggled events while the owner clicked;
        // these probes expose the element that eats the input.
        void ProbeSwitch(ToggleSwitch sw, string name)
        {
            sw.PointerPressed += (_, _) => UiTrace.Log($"{name}.PointerPressed");
            sw.PointerReleased += (_, _) => UiTrace.Log($"{name}.PointerReleased");
        }
        ProbeSwitch(ComStreamSwitch, "ComStreamSwitch");
        ProbeSwitch(ComEmulateSwitch, "ComEmulateSwitch");
        ProbeSwitch(LanStreamSwitch, "LanStreamSwitch");
        ProbeSwitch(LanEmulateSwitch, "LanEmulateSwitch");
        ProbeSwitch(FirmwareSwitch, "FirmwareSwitch");

        this.PointerPressed += (_, e) =>
        {
            var pt = e.GetCurrentPoint(this).Position;
            UiTrace.Log($"page.PointerPressed {pt.X:0},{pt.Y:0}");
            try
            {
                var hits = Microsoft.UI.Xaml.Media.VisualTreeHelper.FindElementsInHostCoordinates(pt, this);
                var top = string.Join(" <- ", hits.Take(10)
                    .Select(h => h is Microsoft.UI.Xaml.FrameworkElement fe
                        ? $"{h.GetType().Name}'{fe.Name}'" : h.GetType().Name));
                UiTrace.Log("hit-test: " + top);
            }
            catch (Exception ex) { UiTrace.Log("hit-test failed: " + ex.Message); }
        };

        Loaded += (_, _) =>
        {
            try
            {
                foreach (var (sw, name) in new[]
                {
                    (ComStreamSwitch, "ComStreamSwitch"), (ComEmulateSwitch, "ComEmulateSwitch"),
                    (LanStreamSwitch, "LanStreamSwitch"), (LanEmulateSwitch, "LanEmulateSwitch"),
                    (FirmwareSwitch, "FirmwareSwitch")
                })
                {
                    var p = sw.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0));
                    UiTrace.Log($"measured {name}: pos {p.X:0},{p.Y:0} size {sw.ActualWidth:0}x{sw.ActualHeight:0} " +
                                $"enabled={sw.IsEnabled} visible={sw.Visibility} hitTest={sw.IsHitTestVisible}");
                }
            }
            catch (Exception ex) { UiTrace.Log("measure failed: " + ex.Message); }
        };

#if DEBUG
        // W13.3 DIAGNOSTIC: dump live layout of ComboBox/TextBox subtrees
            // (жалоба «не поместились 32px») — %TEMP%\emu_layout.txt через 3с после Load.
        Loaded += (_, _) =>
        {
            var timer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var sb = new System.Text.StringBuilder();
                void Walk(Microsoft.UI.Xaml.DependencyObject d, int depth)
                {
                    if (d is Microsoft.UI.Xaml.FrameworkElement fe)
                    {
                        sb.AppendLine($"{new string(' ', depth * 2)}{fe.GetType().Name} '{fe.Name}' " +
                            $"ActualH={fe.ActualHeight:0.#} MinH={fe.MinHeight} " +
                            $"H={(double.IsNaN(fe.Height) ? "NaN" : fe.Height.ToString())} " +
                            $"Margin={fe.Margin} VAlign={fe.VerticalAlignment}" +
                            (d is Microsoft.UI.Xaml.Controls.Control c ? $" Pad={c.Padding} FontSize={c.FontSize}" : ""));
                    }
                    int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(d);
                    for (int i = 0; i < n; i++)
                        Walk(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(d, i), depth + 1);
                }
                Walk(ComPortCombo, 0);
                sb.AppendLine("--- TextBox ---");
                Walk(EquipmentBox, 0);
                using var status = ControlPipeClient.Cmd("status");
                sb.AppendLine("pipe status => " + (status is null ? "NULL" : "OK len=" + status.Raw.RootElement.GetRawText().Length));
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "emu_layout.txt"), sb.ToString());
            };
            timer.Start();

        // W13.4 DIAGNOSTIC: scrollbar state watcher — ground truth для
        // ConsciousStates/ScrollingIndicatorStates + реальной ширины/видимости
        // thumb (проверка kit 30%/45% без помощи cursor injection).
        // %TEMP%\emu_scrollbar.txt, 1с × 15 тиков после Load.
            Microsoft.UI.Xaml.FrameworkElement? FindByType(Microsoft.UI.Xaml.DependencyObject d, string typeName, string? name = null)
            {
                if (d is Microsoft.UI.Xaml.FrameworkElement fe && fe.GetType().Name == typeName &&
                    (name is null || fe.Name == name)) return fe;
                int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < n; i++)
                {
                    var r = FindByType(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(d, i), typeName, name);
                    if (r != null) return r;
                }
                return null;
            }
            void CollectByType(Microsoft.UI.Xaml.DependencyObject d, string typeName, System.Collections.Generic.List<Microsoft.UI.Xaml.FrameworkElement> acc)
            {
                if (d is Microsoft.UI.Xaml.FrameworkElement fe && fe.GetType().Name == typeName) acc.Add(fe);
                int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < n; i++)
                    CollectByType(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(d, i), typeName, acc);
            }
            var log = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "emu_scrollbar.txt");
            System.IO.File.WriteAllText(log, "");
            var barTimer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            var ticks = 0;
            barTimer.Tick += (_, _) =>
            {
                ticks++;
                var bars = new System.Collections.Generic.List<Microsoft.UI.Xaml.FrameworkElement>();
                CollectByType(this, "ScrollBar", bars);
        // страниц шапки = линейный шапочный (max height); остальное (содержимое списков) скроллится
                Microsoft.UI.Xaml.FrameworkElement? bar = null;
                foreach (var b in bars) if (b.ActualHeight > 100 && b.ActualWidth > 0) { bar = b; break; }
                bar ??= bars.Count > 0 ? bars[0] : null;
                if (bar != null)
                {
                    var states = "";
                    if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(bar) > 0 &&
                        Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(bar, 0) is Microsoft.UI.Xaml.FrameworkElement barRoot)
                        foreach (var g in Microsoft.UI.Xaml.VisualStateManager.GetVisualStateGroups(barRoot))
                            states += $"{g.Name}={g.CurrentState?.Name ?? "-"} ";
                    var thumb = FindByType(bar, "Thumb", "VerticalThumb");
                    var thumbInfo = thumb == null ? "thumb=?" :
                        $"thumb W={thumb.ActualWidth:0.#} H={thumb.ActualHeight:0.#} Op={thumb.Opacity:0.##} " +
                        $"Bg={(thumb.GetValue(Microsoft.UI.Xaml.Controls.Control.BackgroundProperty) as Microsoft.UI.Xaml.Media.SolidColorBrush)?.Color.ToString() ?? "?"}";
                    System.IO.File.AppendAllText(log,
                        $"[{DateTime.Now:HH:mm:ss.ff}] bars={bars.Count} barW={bar.ActualWidth:0.#} barH={bar.ActualHeight:0.#} {states}| {thumbInfo}\n");
                }
                else System.IO.File.AppendAllText(log, $"[{DateTime.Now:HH:mm:ss.ff}] no scrollbar\n");
                if (ticks >= 15) barTimer.Stop();
            };
            barTimer.Start();

        // W13.4 DIAGNOSTIC: VSM-forcer — pointer-клики в VM-стенде не
        // доходят до XAML (SetCursorPos/SendInput тихие) ⇒ Collapsed/Expanded
        // переключаю принудительно GoToState. Триггер = файл
        // %TEMP%\emu_vsm.txt с текстом значения ("Collapsed"/"Expanded");
        // результаты → %TEMP%\emu_vsm.log. State-попытки входа/выхода —
        // стоковые для ScrollBar, не меняем; цель = переключить значение.
            var vsmTrig = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "emu_vsm.txt");
            var vsmLog = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "emu_vsm.log");
            if (System.IO.File.Exists(vsmTrig)) System.IO.File.Delete(vsmTrig);
            System.IO.File.WriteAllText(vsmLog, "");
        // W14 DIAGNOSTIC: row-select forcer — триггер %TEMP%\emu_select.txt
        // ("com"/"lan") → выбрать первую строку списка (proof selected-стиля
        // без pointer-клика: soft accent fill, вместительный Fluent-выбор).
            var selTrig = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "emu_select.txt");
            if (System.IO.File.Exists(selTrig)) System.IO.File.Delete(selTrig);
            var selTimer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            selTimer.Tick += (_, _) =>
            {
                if (!System.IO.File.Exists(selTrig)) return;
                string which;
                try { which = System.IO.File.ReadAllText(selTrig).Trim(); System.IO.File.Delete(selTrig); }
                catch { return; }
                if (which == "com" && ComVarsList.Items.Count > 0) ComVarsList.SelectedIndex = 0;
                if (which == "lan" && LanVarsList.Items.Count > 0) LanVarsList.SelectedIndex = 0;
            };
            selTimer.Start();
            var vsmTimer = new Microsoft.UI.Xaml.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            vsmTimer.Tick += (_, _) =>
            {
                if (!System.IO.File.Exists(vsmTrig)) return;
                string state;
                try { state = System.IO.File.ReadAllText(vsmTrig).Trim(); System.IO.File.Delete(vsmTrig); }
                catch { return; }
                var bars = new System.Collections.Generic.List<Microsoft.UI.Xaml.FrameworkElement>();
                CollectByType(this, "ScrollBar", bars);
                var idx = 0;
                foreach (var b in bars)
                {
                    if (b.ActualHeight <= 100 || b.ActualWidth <= 0 || b is not Microsoft.UI.Xaml.Controls.Control bc) continue;
                    var pt = b.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
                    var ok = Microsoft.UI.Xaml.VisualStateManager.GoToState(bc, state, false);
                    var thumb = FindByType(b, "Thumb", "VerticalThumb");
                    var bg = thumb?.GetValue(Microsoft.UI.Xaml.Controls.Control.BackgroundProperty) as Microsoft.UI.Xaml.Media.SolidColorBrush;
        // ThumbVisual = Rectangle потому шаблон thumb-template: проверяем,
        // что TemplateBinding Fill реально следует за Background
                    var visual = thumb == null ? null : FindByType(thumb, "Rectangle", "ThumbVisual");
                    var fill = visual?.GetValue(Microsoft.UI.Xaml.Shapes.Shape.FillProperty) as Microsoft.UI.Xaml.Media.SolidColorBrush;
                    System.IO.File.AppendAllText(vsmLog,
                        $"[{DateTime.Now:HH:mm:ss.ff}] [{idx}] rel={pt.X:0},{pt.Y:0} {b.ActualWidth:0}x{b.ActualHeight:0} " +
                        $"GoToState({state}) => {ok}; thumbBg={(bg != null ? bg.Color.ToString() : "-")} visualFill={(fill != null ? fill.Color.ToString() : "-")}\n");
                    idx++;
                }
            };
            vsmTimer.Start();
        };
#endif
    }

    private static bool TryNum(string text, out double v) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ||
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out v);

    private void DisposeViewModel()
    {
        if (_vmDisposed) return;
        _vmDisposed = true;
        _vm.Dispose();
    }

    private void RenderSnapshot(EmuSnapshot s)
    {
        // P1 2026-08-26 («переключатели не работают»): a FAILED poll (dead
        // engine / pipe hiccup) yields Ok=false — forcing the switches to the
        // Empty snapshot's OFF state every second fought the operator's toggle
        // and looked like "the switch snaps back". Keep the last known render;
        // when the engine PROCESS dies, Running→false shows the stub anyway.
        if (!s.Ok)
            return;

        _syncingSwitches = true;
        if (!_busyCom) ComStreamSwitch.IsOn = s.ComStreaming;
        if (!_busyLan) LanStreamSwitch.IsOn = s.LanStreaming;
        if (!_busyFirmware) FirmwareSwitch.IsOn = s.LanFirmwareOn;
        if (!_busyEmulate)
        {
            ComEmulateSwitch.IsOn = s.ComEmulating;
            LanEmulateSwitch.IsOn = s.LanEmulating;
        }
        if (s.UnitsPresetIndex >= 0 && s.UnitsPresetIndex < PresetItems.Length &&
            PresetCombo.SelectedIndex != s.UnitsPresetIndex)
            PresetCombo.SelectedIndex = s.UnitsPresetIndex;
        _syncingSwitches = false;

            ComStreamText.Text = s.ComStreaming
                ? $"поток COM включён — {s.ComPort}"
                : "поток COM выключен";
            ComDetailText.Text = $"шаблон: {s.ComTemplate} · {s.ComLines}";
            LanStreamText.Text = s.LanStreaming
                ? $"поток LAN включён — {s.LanAdapter}"
                : "поток LAN выключен";
            // 2026-08-29 (frame-sent feedback): the operator verifies emission
            // by the sent-frames counter; the iCem-side receive requirement
            // (operator-domain NIC binding) is stated honestly while streaming.
            LanDetailText.Text =
                $"оборудование {s.LanEquipment} · SV: {s.LanSvCount} · firmware: {s.LanFirmware} · кадров отправлено: {s.LanFramesSent}" +
                (s.LanStreaming ? " · для приёма в iCem нужен LAN-комплект на этом адаптере" : "");

            // 2026-08-29 (discoverability): LAN truth visible WITHOUT opening
            // the LAN tab — one line in the page header off the same snapshot.
            LanHeaderStatus.Text =
                !s.LanNpcapAvailable ? "LAN: ⚠ Npcap не установлен" :
                s.LanStreaming ? $"LAN: стрим → {s.LanAdapter}" :
                s.LanAdapter is "—" or "" ? "LAN: ⚠ адаптер не выбран" :
                "LAN: выкл";
            LanHeaderStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Resources[
                LanHeaderStatus.Text.StartsWith("LAN: ⚠", StringComparison.Ordinal)
                    ? "MacOrangeBrush" : "MacLabel2Brush"];

            UnitsPresetText.Text = "Пресет единиц: " + s.UnitsPreset;
        UnitsOverridesText.Text =
                "Переопределения: " +
                $"расход — {OvText(s.UnitFlowOv)} · температура — {OvText(s.UnitTemperatureOv)} · " +
                $"плотность — {OvText(s.UnitDensityOv)} · объём — {OvText(s.UnitVolumeOv)} · " +
                $"уровень — {OvText(s.UnitLevelOv)} · давление — {OvText(s.UnitPressureOv)}";

        if (!_equipmentFocused && s.LanEquipment is not ("—" or ""))
            EquipmentBox.Text = s.LanEquipment;

        EmuUptime.Text = "uptime " + s.Uptime;
        EmuPid.Text = "PID " + s.Pid;
    }

        private static string OvText(string ov) => string.IsNullOrEmpty(ov) ? "по пресету" : ov;
}
