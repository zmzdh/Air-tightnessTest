using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace LumbarMassageTest.Services
{
    public class LoginPreferences
    {
        public string? LastUsername { get; set; }
        public Dictionary<string, string> RememberedPasswords { get; set; } = new();
    }

    public class UserPreferenceService
    {
        private readonly string _filePath;
        private readonly JsonSerializerOptions _jsonOptions = new()
        {
            WriteIndented = true
        };

        public LoginPreferences Preferences { get; private set; } = new();

        public UserPreferenceService()
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var folder = Path.Combine(appDataPath, "LumbarMassageTest");
            Directory.CreateDirectory(folder);
            _filePath = Path.Combine(folder, "login_preferences.json");

            Load();
        }

        public void Save(string username, string password, bool rememberPassword)
        {
            Preferences.LastUsername = username;

            if (rememberPassword && !string.IsNullOrWhiteSpace(username))
            {
                Preferences.RememberedPasswords[username] = Convert.ToBase64String(Encoding.UTF8.GetBytes(password));
            }
            else if (!string.IsNullOrWhiteSpace(username) && Preferences.RememberedPasswords.ContainsKey(username))
            {
                Preferences.RememberedPasswords.Remove(username);
            }

            Persist();
        }

        public bool TryGetPassword(string username, out string password)
        {
            password = string.Empty;

            if (Preferences.RememberedPasswords.TryGetValue(username, out var encodedPassword))
            {
                try
                {
                    password = Encoding.UTF8.GetString(Convert.FromBase64String(encodedPassword));
                    return true;
                }
                catch (FormatException)
                {
                    Preferences.RememberedPasswords.Remove(username);
                    Persist();
                }
            }

            return false;
        }

        private void Load()
        {
            try
            {
                if (File.Exists(_filePath))
                {
                    var json = File.ReadAllText(_filePath);
                    var preferences = JsonSerializer.Deserialize<LoginPreferences>(json, _jsonOptions);
                    if (preferences != null)
                    {
                        Preferences = preferences;
                    }
                }
            }
            catch
            {
                Preferences = new LoginPreferences();
            }
        }

        private void Persist()
        {
            try
            {
                var json = JsonSerializer.Serialize(Preferences, _jsonOptions);
                File.WriteAllText(_filePath, json);
            }
            catch
            {
                // ignore persistence errors to avoid blocking login
            }
        }
    }
}
