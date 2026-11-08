using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ZapretUi
{
    public partial class MainWindow
    {
        void WireSettings()
        {
            CbGame.SelectionChanged += (s, e) => Safe.Run(() =>
            {
                if (Ctx.Loading) return;
                string mode = (string)((ComboBoxItem)CbGame.SelectedItem).Tag;
                Zapret.SetGameFilter(mode);
                UpdateGamePortsVisibility(mode);
                SetSettingsChanged();
            });

            // Порты игрового фильтра: проверка как в service.bat, сохранение вместе с текущим режимом
            void SavePorts()
            {
                string tcp = Zapret.TestPortRange(TbTcpRange.Text);
                string udp = Zapret.TestPortRange(TbUdpRange.Text);
                var bad = new System.Collections.Generic.List<string>();
                if (tcp == null) bad.Add("TCP");
                if (udp == null) bad.Add("UDP");
                if (bad.Count > 0)
                {
                    TxtPortsHint.Foreground = Theme.Pal["Bad"];
                    TxtPortsHint.Text = $"Не получилось разобрать порты {string.Join(" и ", bad)}. Нужны числа от 1 до 65535 или диапазоны через запятую, например 1024-1934,1936-65535.";
                    return;
                }
                string mode = Zapret.GetServiceGameFilter().Mode;
                Zapret.SetGameFilter(mode, tcp, udp);
                LoadSettings();
                TxtPortsHint.Foreground = Theme.Pal["Muted"];
                TxtPortsHint.Text = $"Сохранено: TCP {tcp}, UDP {udp}.";
                SetSettingsChanged();
            }
            BtnSavePorts.Click += (s, e) => Safe.Run(SavePorts);
            BtnResetPorts.Click += (s, e) => Safe.Run(() =>
            {
                TbTcpRange.Text = "1024-65535";
                TbUdpRange.Text = "1024-65535";
                SavePorts();
            });
            TbTcpRange.KeyDown += (s, e) => { if (e.Key == Key.Return) { e.Handled = true; Safe.Run(SavePorts); } };
            TbUdpRange.KeyDown += (s, e) => { if (e.Key == Key.Return) { e.Handled = true; Safe.Run(SavePorts); } };

            CbIpset.SelectionChanged += (s, e) =>
            {
                if (Ctx.Loading) return;
                try
                {
                    Zapret.SetIpsetStatus((string)((ComboBoxItem)CbIpset.SelectedItem).Tag);
                    SetSettingsChanged();
                }
                catch (Exception ex) { MessageDialog.ShowError(ex.Message); }
                LoadSettings();
            };
            BtnIpsetUpdate.Click += (s, e) =>
            {
                Cursor = Cursors.Wait;
                try
                {
                    try
                    {
                        Busy("Скачиваю список IPset", Zapret.UpdateIpsetListCore);
                        SetSettingsChanged();
                        TxtSettingsMsg.Text = "Список IPset обновлён. " + TxtSettingsMsg.Text;
                    }
                    catch (Exception ex) { MessageDialog.ShowError(ex.Message); }
                }
                finally { Cursor = null; }
                LoadSettings();
            };

            CbTheme.SelectionChanged += (s, e) => Safe.Run(() =>
            {
                if (Ctx.Loading) return;
                Ctx.UiState.ThemeMode = (string)((ComboBoxItem)CbTheme.SelectedItem).Tag;
                Ctx.UiState.Save();
                Theme.Sync();
            });
            ChkSystemAccent.Click += (s, e) => Safe.Run(() =>
            {
                Ctx.UiState.SystemAccent = ChkSystemAccent.IsChecked == true;
                Ctx.UiState.Save();
                Theme.Sync();
            });
            ChkAskBeforeTest.Click += (s, e) => Safe.Run(() =>
            {
                Ctx.UiState.AutoRemoveServiceForTest = ChkAskBeforeTest.IsChecked != true;
                Ctx.UiState.Save();
            });
            ChkUpdates.Click += (s, e) => Safe.Run(() =>
            {
                if (ChkUpdates.IsChecked == true) { Fs.WriteTextFile(Ctx.UpdFlag, new[] { "ENABLED" }); Updater.StartUpdateCheck(); }
                else { try { File.Delete(Ctx.UpdFlag); } catch { } }
            });
            ChkAskBeforeStart.Click += (s, e) => Safe.Run(() =>
            {
                Ctx.UiState.ServiceStartAction = ChkAskBeforeStart.IsChecked == true ? "ask" : "auto";
                Ctx.UiState.Save();
            });

            BtnUpdate.Click += (s, e) => Safe.Run(() => ShowUpdateFlow(Zapret.GetLocalVersion(), Ctx.NewVersion));
            BtnMigrate.Click += (s, e) => Safe.Run(StartMigration);
            BtnExport.Click += (s, e) => Safe.Run(ExportSettings);
            BtnImport.Click += (s, e) => Safe.Run(ImportSettings);
            BtnDiagnostics.Click += (s, e) => Safe.Run(() => { DiagnosticsDialog.Show(this); UpdateStatus(); });

            BtnShortcut.Click += (s, e) => Safe.Run(() =>
            {
                string p = Shortcuts.CreateShortcut(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
                MessageDialog.Show("Ярлык создан:\n" + p);
            });
            BtnShortcutStart.Click += (s, e) => Safe.Run(() =>
            {
                string p = Shortcuts.CreateShortcut(Environment.GetFolderPath(Environment.SpecialFolder.Programs));
                MessageDialog.Show("Ярлык добавлен в меню «Пуск»:\n" + p + "\n\nЕго можно закрепить на начальном экране или на панели задач.");
            });
        }

        static void SelectComboTag(ComboBox combo, string tag)
        {
            foreach (ComboBoxItem item in combo.Items)
                if ((string)item.Tag == tag) { combo.SelectedItem = item; return; }
        }

        // Поля портов только для включённых протоколов: выключен — ничего, TCP/UDP — своё поле, «TCP и UDP» — оба
        void UpdateGamePortsVisibility(string mode)
        {
            if (!Zapret.TestGameFilterRanges() || mode == "off") { GameRanges.Visibility = Visibility.Collapsed; return; }
            GameRanges.Visibility = Visibility.Visible;
            GameTcpRow.Visibility = (mode == "all" || mode == "tcp") ? Visibility.Visible : Visibility.Collapsed;
            GameUdpRow.Visibility = (mode == "all" || mode == "udp") ? Visibility.Visible : Visibility.Collapsed;
        }

        public void LoadSettings()
        {
            Ctx.Loading = true;
            var gf = Zapret.GetServiceGameFilter();
            SelectComboTag(CbGame, gf.Mode);
            if (Zapret.TestGameFilterRanges())
            {
                TxtGameInfo.Text = "Пускает через обход игровые порты для адресов из IPset. Нужен для игр.";
                TbTcpRange.Text = gf.TcpRange ?? "1024-65535";
                TbUdpRange.Text = gf.UdpRange ?? "1024-65535";
                UpdateGamePortsVisibility(gf.Mode);
            }
            else
            {
                // До 1.10.4 диапазоны не настраиваются
                TxtGameInfo.Text = "Пускает через обход порты 1024–65535 для адресов из IPset. Нужен для игр. Свои диапазоны портов появились в zapret 1.10.4.";
                GameRanges.Visibility = Visibility.Collapsed;
            }
            string ip = Zapret.GetIpsetStatus();
            SelectComboTag(CbIpset, ip);
            TxtIpsetInfo.Text = ip switch
            {
                "loaded" => "Обход применяется к IP-адресам из ipset-all.txt.",
                "any" => "Обход применяется ко всем IP-адресам. Может мешать другим сайтам и играм.",
                _ => "Список IP не используется, работают только списки доменов.",
            };
            if (!File.Exists(Ctx.IpsetBak) && ip != "loaded") TxtIpsetInfo.Text += " Для режима loaded сначала обновите список.";
            ChkUpdates.IsChecked = File.Exists(Ctx.UpdFlag);
            ChkAskBeforeTest.IsChecked = !Ctx.UiState.AutoRemoveServiceForTest;
            SelectComboTag(CbTheme, Ctx.UiState.ThemeMode);
            ChkSystemAccent.IsChecked = Ctx.UiState.SystemAccent;
            ChkAskBeforeStart.IsChecked = Ctx.UiState.ServiceStartAction != "auto";
            Ctx.Loading = false;
        }

        void SetSettingsChanged()
        {
            var st = Zapret.GetState();
            SettingsMsgPanel.Visibility = Visibility.Visible;
            if (st.Mode == "service" || st.Mode == "manual")
            {
                TxtSettingsMsg.Text = "Изменения вступят в силу после перезапуска стратегии.";
                BtnApplySettings.Visibility = Visibility.Visible;
            }
            else
            {
                TxtSettingsMsg.Text = "Изменения применятся при следующем запуске стратегии.";
                BtnApplySettings.Visibility = Visibility.Collapsed;
            }
        }

        // ---------- migration / export / import ----------

        void StartMigration()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Выберите service.bat в папке старой версии zapret",
                Filter = "service.bat|service.bat",
                InitialDirectory = Directory.GetParent(Ctx.Root).FullName,
            };
            if (dlg.ShowDialog(this) != true) return;
            string dir = Path.GetDirectoryName(dlg.FileName);
            if (Zapret.TestSameFolder(dir, Ctx.Root)) throw new Exception("Это текущая папка zapret. Выберите service.bat в папке старой версии.");
            if (!Directory.Exists(Path.Combine(dir, "lists")) || !Directory.Exists(Path.Combine(dir, "utils")))
                throw new Exception($"В папке {dir} нет lists и utils — это не похоже на zapret-discord-youtube.");
            var items = Migration.GetMigrationItems(dir);
            if (items.Count == 0)
            {
                MessageDialog.Show($"В папке {dir} нет ничего, что отличалось бы от этой: свои списки пустые, настройки совпадают.");
                return;
            }
            var ids = ChecklistDialog.Show(this, "Перенос настроек", "Что перенести из старой папки",
                $"Из: {dir}\nВ: {Ctx.Root}", items, "Перенести",
                "Файлы этой папки, которые будут заменены, сначала сохранятся в utils\\migrate-backup-…");
            if (ids == null || ids.Length == 0) return;
            var res = Migration.InvokeMigration(dir, ids);
            UpdateAfterMigration();

            string msg = "Перенесено:\n  " + string.Join("\n  ", res.Done);
            if (res.Backup != null) msg += $"\n\nПрежние файлы этой папки сохранены в\n{res.Backup}";

            // Служба установлена из старой папки — предлагаем поставить её отсюда с той же стратегией
            string svcRoot = Zapret.GetServiceRoot();
            var state = Zapret.GetState();
            if (svcRoot != null && !Zapret.TestSameFolder(svcRoot, Ctx.Root) && state.ServiceStrategy != null
                && CbStrategy.Items.Contains(state.ServiceStrategy))
            {
                var a = MessageDialog.Show(
                    $"{msg}\n\nСлужба zapret установлена из другой папки:\n{svcRoot}\n\nПереустановить её из этой папки со стратегией {state.ServiceStrategy}?",
                    MessageKind.Question, true, "Переустановить", "Не сейчас");
                if (a == MessageResult.Yes)
                {
                    Cursor = Cursors.Wait;
                    try { InstallZapretService(state.ServiceStrategy); }
                    finally { Cursor = null; }
                    UpdateStatus();
                }
            }
            else
            {
                MessageDialog.Show(msg);
            }
        }

        void ExportSettings()
        {
            var ids = ChecklistDialog.Show(this, "Экспорт настроек", "Что сохранить в архив",
                "Получится один .zip: его можно импортировать в другую папку zapret или на другом компьютере.",
                Migration.GetExportItems(), "Сохранить",
                "Пароли и личные данные в архив не попадают: только списки доменов, настройки окна и zapret.");
            if (ids == null || ids.Length == 0) return;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Сохранить настройки zapret",
                Filter = "Архив настроек (*.zip)|*.zip",
                FileName = "zapret-настройки-" + DateTime.Now.ToString("yyyy-MM-dd") + ".zip",
            };
            if (dlg.ShowDialog(this) != true) return;
            Cursor = Cursors.Wait;
            try { Migration.NewSettingsArchive(dlg.FileName, ids); }
            finally { Cursor = null; }
            MessageDialog.Show("Настройки сохранены:\n" + dlg.FileName);
        }

        void ImportSettings()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Выберите архив настроек zapret",
                Filter = "Архив настроек (*.zip)|*.zip",
            };
            if (dlg.ShowDialog(this) != true) return;
            string tmp = Path.Combine(Path.GetTempPath(), "zapret-import-" + Guid.NewGuid().ToString("N"));
            try
            {
                ZipFile.ExtractToDirectory(dlg.FileName, tmp);
                string mf = Path.Combine(tmp, Migration.ExportManifest);
                if (!File.Exists(mf)) throw new Exception("Это не архив настроек окна zapret: в нём нет описания zapret-ui-export.json.");
                using var doc = JsonDocument.Parse(File.ReadAllText(mf));
                var m = doc.RootElement;
                string created = m.TryGetProperty("Created", out var c) ? c.GetString() : "";
                string zapretVersion = m.TryGetProperty("ZapretVersion", out var z) ? z.GetString() : "?";
                var archiveItems = m.TryGetProperty("Items", out var it) && it.ValueKind == JsonValueKind.Array
                    ? it.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToArray()
                    : Array.Empty<string>();
                var items = Migration.GetMigrationItems(tmp, true, archiveItems);
                if (items.Count == 0)
                {
                    MessageDialog.Show("В архиве нет ничего, что отличалось бы от текущих настроек.");
                    return;
                }
                string when;
                try { when = DateTime.Parse(created).ToString("dd.MM.yyyy HH:mm"); }
                catch { when = created; }
                var ids = ChecklistDialog.Show(this, "Импорт настроек", "Что импортировать из архива",
                    $"{Path.GetFileName(dlg.FileName)} — сохранён {when} в zapret {zapretVersion}",
                    items, "Импортировать",
                    "Файлы этой папки, которые будут заменены, сначала сохранятся в utils\\migrate-backup-…");
                if (ids == null || ids.Length == 0) return;
                var res = Migration.InvokeMigration(tmp, ids);
                UpdateAfterMigration();
                string msg = "Импортировано:\n  " + string.Join("\n  ", res.Done);
                if (res.Backup != null) msg += $"\n\nПрежние файлы этой папки сохранены в\n{res.Backup}";
                MessageDialog.Show(msg);
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }
    }
}
