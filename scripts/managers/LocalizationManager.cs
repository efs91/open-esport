using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace Openesport.Managers
{
    public static class LocalizationManager
    {
        private static Dictionary<string, string> _translations = new();
        private static Dictionary<string, string> _defaultTranslations = new(); // Traductions en français
        private static LogManager _logManager = new LogManager();

        private const string LanguageFile = "user://language.cfg";

        /// <summary>La langue choisie la dernière fois (français sinon) : elle est conservée d'une session à l'autre.</summary>
        public static string SavedLanguage
        {
            get { var cfg = new ConfigFile(); return cfg.Load(LanguageFile) == Error.Ok ? (string)cfg.GetValue("language", "code", "français") : "français"; }
            set { var cfg = new ConfigFile(); cfg.SetValue("language", "code", value); cfg.Save(LanguageFile); }
        }

        public static void LoadLanguage(string languageCode)
        {
            _logManager.Info($"[LocalizationManager] Chargement de la langue : {languageCode}");
            
            try
            {
                // D'abord charger le français comme langue par défaut
                string defaultFilePath = "res://localization/français.lang";
                if (FileAccess.FileExists(defaultFilePath))
                {
                    using var defaultFile = FileAccess.Open(defaultFilePath, FileAccess.ModeFlags.Read);
                    string defaultJsonContent = defaultFile.GetAsText();
                    _defaultTranslations = JsonSerializer.Deserialize<Dictionary<string, string>>(defaultJsonContent) ?? new();
                    _logManager.Info($"[LocalizationManager] {_defaultTranslations.Count} traductions par défaut chargées");
                }
                
                // Ensuite charger la langue sélectionnée
                string filePath = $"res://localization/{languageCode}.lang";
                if (FileAccess.FileExists(filePath))
                {
                    using var file = FileAccess.Open(filePath, FileAccess.ModeFlags.Read);
                    string jsonContent = file.GetAsText();
                    var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(jsonContent);
                    
                    if (translations != null)
                    {
                        _translations = translations;
                        _logManager.Info($"[LocalizationManager] {_translations.Count} traductions chargées");
                    }
                    else
                    {
                        _logManager.Warning($"[LocalizationManager] Aucune traduction trouvée dans le fichier {filePath}");
                    }
                }
                else
                {
                    _logManager.Warning($"[LocalizationManager] Fichier de langue non trouvé : {filePath}");
                }
            }
            catch (Exception ex)
            {
                _logManager.Error($"[LocalizationManager] Erreur lors du chargement de la langue : {ex.Message}");
            }
        }

        /// <summary>
        /// Traduit un texte écrit en français dans le code : le texte français sert de clé. Cherche la traduction dans la langue choisie ; sans traduction (ou en français),
        /// le texte est rendu tel quel. Pas d'avertissement : un texte non traduit s'affiche simplement en français.
        /// </summary>
        public static string Translate(string french)
        {
            if (string.IsNullOrEmpty(french)) return french;
            return _translations.TryGetValue(french, out string value) && !string.IsNullOrEmpty(value) ? value : french;
        }

        public static string GetTranslation(string key)
        {
            // D'abord chercher dans la langue sélectionnée
            if (_translations.TryGetValue(key, out string value))
            {
                return value;
            }
            
            // Si non trouvé, chercher dans le français (langue par défaut)
            if (_defaultTranslations.TryGetValue(key, out string defaultValue))
            {
                _logManager.Warning($"[LocalizationManager] Utilisation de la traduction par défaut pour : {key}");
                return defaultValue;
            }
            
            _logManager.Warning($"[LocalizationManager] Clé de traduction non trouvée : {key}");
            return key;
        }
    }
} 