using ManagedShell.Common.Helpers;
using ManagedShell.Common.Logging;
using System;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace RetroBar.Utilities
{
    public class SettingsManager<T> : INotifyPropertyChanged where T : IMigratableSettings
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private static JsonSerializerOptions options = new()
        {
            IgnoreReadOnlyProperties = true,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        private string _fileName;

        // AERO: live reload. The AERO Control Center edits this file while the
        // taskbar runs; external changes are applied to the live settings object
        // so every binding updates immediately.
        private FileSystemWatcher _watcher;
        private Timer _reloadTimer;
        private string _lastText;
        private bool _applyingExternal;

        private T _settings;
        public T Settings
        {
            get => _settings;
            set
            {
                _settings = value;
                OnPropertyChanged();
                SaveToFile();
            }
        }

        public SettingsManager(string fileName, T defaultSettings)
        {
            _fileName = fileName;
            _settings = defaultSettings;

            if (!LoadFromFile())
            {
                ShellLogger.Info("SettingsManager: Using default settings");
            }

            StartWatching();
        }

        private void StartWatching()
        {
            try
            {
                string dir = Path.GetDirectoryName(_fileName);
                Directory.CreateDirectory(dir);
                _reloadTimer = new Timer(_ => ReloadFromExternalChange(), null, Timeout.Infinite, Timeout.Infinite);
                _watcher = new FileSystemWatcher(dir, Path.GetFileName(_fileName))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
                };
                // Debounce: editors write in several steps (temp file + rename).
                FileSystemEventHandler onChange = (_, _) => _reloadTimer.Change(250, Timeout.Infinite);
                _watcher.Changed += onChange;
                _watcher.Created += onChange;
                _watcher.Renamed += (_, _) => _reloadTimer.Change(250, Timeout.Infinite);
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                ShellLogger.Error($"SettingsManager: Unable to watch settings file: {ex.Message}");
            }
        }

        private void ReloadFromExternalChange()
        {
            string text;
            try
            {
                text = File.ReadAllText(_fileName);
            }
            catch (Exception ex)
            {
                ShellLogger.Warning($"SettingsManager: Unable to read changed settings: {ex.Message}");
                return;
            }

            // Ignore our own saves.
            if (text == _lastText)
            {
                return;
            }

            T incoming;
            try
            {
                incoming = JsonSerializer.Deserialize<T>(text);
            }
            catch (Exception ex)
            {
                ShellLogger.Warning($"SettingsManager: Ignoring invalid external settings: {ex.Message}");
                return;
            }

            _lastText = text;
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => ApplyExternal(incoming)));
        }

        private void ApplyExternal(T incoming)
        {
            _applyingExternal = true;
            try
            {
                foreach (PropertyInfo prop in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!prop.CanRead || !prop.CanWrite || prop.GetIndexParameters().Length > 0)
                    {
                        continue;
                    }

                    object newValue = prop.GetValue(incoming);
                    object oldValue = prop.GetValue(_settings);
                    if (!Equals(newValue, oldValue))
                    {
                        prop.SetValue(_settings, newValue);
                    }
                }
                ShellLogger.Info("SettingsManager: Applied external settings change");
            }
            finally
            {
                _applyingExternal = false;
            }
        }

        private bool LoadFromFile()
        {
            try
            {
                if (!ShellHelper.Exists(_fileName))
                {
                    return false;
                }

                string jsonString = File.ReadAllText(_fileName);
                _lastText = jsonString;
                _settings = JsonSerializer.Deserialize<T>(jsonString);

                if (_settings.MigrationPerformed)
                {
                    // Save post-migration state so that we don't need to migrate every startup
                    SaveToFile();
                }

                return true;
            }
            catch (Exception ex)
            {
                ShellLogger.Error($"SettingsManager: Error loading settings file: {ex.Message}");
                return false;
            }
        }

        private void SaveToFile()
        {
            if (_applyingExternal)
            {
                // The file already holds these values; rewriting it would only
                // trigger another reload.
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_fileName));

                string jsonString = JsonSerializer.Serialize(Settings, options);
                _lastText = jsonString;
                File.WriteAllText(_fileName, jsonString);
            }
            catch (Exception ex)
            {
                ShellLogger.Error($"SettingsManager: Error saving settings file: {ex.Message}");
            }
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public interface IMigratableSettings
    {
        public bool MigrationPerformed { get; }
    }
}