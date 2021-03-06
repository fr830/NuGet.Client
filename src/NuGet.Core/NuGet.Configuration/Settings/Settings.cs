// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using NuGet.Common;

namespace NuGet.Configuration
{
    /// <summary>
    /// Concrete implementation of ISettings to support NuGet Settings
    /// Wrapper for computed settings from given settings files
    /// </summary>
    public class Settings : ISettings
    {
        /// <summary>
        /// Default file name for a settings file is 'NuGet.config'
        /// Also, the user level setting file at '%APPDATA%\NuGet' always uses this name
        /// </summary>
        public static readonly string DefaultSettingsFileName = "NuGet.Config";

        /// <summary>
        /// NuGet config names with casing ordered by precedence.
        /// </summary>
        public static readonly string[] OrderedSettingsFileNames =
            PathUtility.IsFileSystemCaseInsensitive ?
            new[] { DefaultSettingsFileName } :
            new[]
            {
                "nuget.config", // preferred style
                "NuGet.config", // Alternative
                DefaultSettingsFileName  // NuGet v2 style
            };

        public static readonly string[] SupportedMachineWideConfigExtension =
            RuntimeEnvironmentHelper.IsWindows ?
            new[] { "*.config" } :
            new[] { "*.Config", "*.config" };

        private SettingsFile _settingsHead { get; }

        private Dictionary<string, VirtualSettingSection> _computedSections { get; set; }

        public SettingSection GetSection(string sectionName)
        {
            if (_computedSections.TryGetValue(sectionName, out var section))
            {
                return section.Clone() as SettingSection;
            }

            return null;
        }

        public void AddOrUpdate(string sectionName, SettingItem item)
        {
            if (string.IsNullOrEmpty(sectionName))
            {
                throw new ArgumentException(Resources.Argument_Cannot_Be_Null_Or_Empty, nameof(sectionName));
            }

            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            // Operation is an update
            if (_computedSections.TryGetValue(sectionName, out var section) && section.Items.Contains(item))
            {
                // An update could not be possible here because the operation might be
                // in a machine wide config. If so then we want to add the item to
                // the output config.
                if (section.Update(item))
                {
                    return;
                }
            }

            // Operation is an add
            var outputSettingsFile = GetOutputSettingFileForSection(sectionName);
            if (outputSettingsFile == null)
            {
                throw new InvalidOperationException(Resources.NoWritteableConfig);
            }

            AddOrUpdate(outputSettingsFile, sectionName, item);
        }

        internal void AddOrUpdate(SettingsFile settingsFile, string sectionName, SettingItem item)
        {
            if (string.IsNullOrEmpty(sectionName))
            {
                throw new ArgumentException(Resources.Argument_Cannot_Be_Null_Or_Empty, nameof(sectionName));
            }

            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            var currentSettings = Priority.Last(f => f.Equals(settingsFile));
            if (settingsFile.IsMachineWide || (currentSettings?.IsMachineWide ?? false))
            {
                throw new InvalidOperationException(Resources.CannotUpdateMachineWide);
            }

            if (currentSettings == null)
            {
                Priority.First().SetNextFile(settingsFile);
            }

            // If it is an update this will take care of it and modify the underlaying object, which is also referenced by _computedSections.
            settingsFile.AddOrUpdate(sectionName, item);

            // AddOrUpdate should have created this section, therefore this should always exist.
            settingsFile.TryGetSection(sectionName, out var settingFileSection);

            // If it is an add we have to manually add it to the _computedSections.
            var computedSectionExists = _computedSections.TryGetValue(sectionName, out var section);
            if (computedSectionExists && !section.Items.Contains(item))
            {
                var existingItem = settingFileSection.Items.First(i => i.Equals(item));
                section.Add(existingItem);
            }
            else if (!computedSectionExists)
            {
                _computedSections.Add(sectionName,
                    new VirtualSettingSection(settingFileSection));
            }
        }

        public void Remove(string sectionName, SettingItem item)
        {
            if (string.IsNullOrEmpty(sectionName))
            {
                throw new ArgumentException(Resources.Argument_Cannot_Be_Null_Or_Empty, nameof(sectionName));
            }

            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            if (!_computedSections.TryGetValue(sectionName, out var section))
            {
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, Resources.SectionDoesNotExist, sectionName));
            }

            if (!section.Items.Contains(item))
            {
                throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, Resources.ItemDoesNotExist, sectionName));
            }

            section.Remove(item);

            if (section.IsEmpty())
            {
                _computedSections.Remove(sectionName);
            }
        }

        public event EventHandler SettingsChanged = delegate { };

        public Settings(string root)
            : this(new SettingsFile(root)) { }

        public Settings(string root, string fileName)
            : this(new SettingsFile(root, fileName)) { }

        public Settings(string root, string fileName, bool isMachineWide)
            : this(new SettingsFile(root, fileName, isMachineWide)) { }

        internal Settings(SettingsFile settingsHead)
        {
            _settingsHead = settingsHead;
            var computedSections = new Dictionary<string, VirtualSettingSection>();

            var curr = _settingsHead;
            while (curr != null)
            {
                curr.MergeSectionsInto(computedSections);
                curr = curr.Next;
            }

            _computedSections = computedSections;
        }

        private SettingsFile GetOutputSettingFileForSection(string sectionName)
        {
            // Search for the furthest from the user that can be written
            // to that is not clearing the ones before it on the hierarchy
            var writteableSettingsFiles = Priority.Where(f => !f.IsMachineWide);

            var clearedSections = writteableSettingsFiles.Select(f => {
                if(f.TryGetSection(sectionName, out var section))
                {
                    return section;
                }
                return null;
            }).Where(s => s != null && s.Items.Contains(new ClearItem()));

            if (clearedSections.Any())
            {
                return clearedSections.First().Origin;
            }

            // if none have a clear tag, default to furthest from the user
            return writteableSettingsFiles.LastOrDefault();
        }

        /// <summary>
        /// Enumerates the sequence of <see cref="SettingsFile"/> instances
        /// ordered from closer to user to further
        /// </summary>
        internal IEnumerable<SettingsFile> Priority
        {
            get
            {
                // explore the linked list, terminating when a duplicate path is found
                var current = _settingsHead;
                var found = new List<SettingsFile>();
                var paths = new HashSet<string>();
                while (current != null && paths.Add(current.ConfigFilePath))
                {
                    found.Add(current);
                    current = current.Next;
                }

                return found
                    .OrderByDescending(s => s.Priority);
            }
        }

        public void SaveToDisk()
        {
            foreach(var settingsFile in Priority)
            {
                settingsFile.SaveToDisk();
            }

            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Load default settings based on a directory.
        /// This includes machine wide settings.
        /// </summary>
        public static ISettings LoadDefaultSettings(string root)
        {
            return LoadSettings(
                root,
                configFileName: null,
                machineWideSettings: new XPlatMachineWideSetting(),
                loadUserWideSettings: true,
                useTestingGlobalPath: false);
        }

        /// <summary>
        /// Loads user settings from the NuGet configuration files. The method walks the directory
        /// tree in <paramref name="root" /> up to its root, and reads each NuGet.config file
        /// it finds in the directories. It then reads the user specific settings,
        /// which is file <paramref name="configFileName" />
        /// in <paramref name="root" /> if <paramref name="configFileName" /> is not null,
        /// If <paramref name="configFileName" /> is null, the user specific settings file is
        /// %AppData%\NuGet\NuGet.config.
        /// After that, the machine wide settings files are added.
        /// </summary>
        /// <remarks>
        /// For example, if <paramref name="root" /> is c:\dir1\dir2, <paramref name="configFileName" />
        /// is "userConfig.file", the files loaded are (in the order that they are loaded):
        /// c:\dir1\dir2\nuget.config
        /// c:\dir1\nuget.config
        /// c:\nuget.config
        /// c:\dir1\dir2\userConfig.file
        /// machine wide settings (e.g. c:\programdata\NuGet\Config\*.config)
        /// </remarks>
        /// <param name="root">
        /// The file system to walk to find configuration files.
        /// Can be null.
        /// </param>
        /// <param name="configFileName">The user specified configuration file.</param>
        /// <param name="machineWideSettings">
        /// The machine wide settings. If it's not null, the
        /// settings files in the machine wide settings are added after the user sepcific
        /// config file.
        /// </param>
        /// <returns>The settings object loaded.</returns>
        public static ISettings LoadDefaultSettings(
            string root,
            string configFileName,
            IMachineWideSettings machineWideSettings)
        {
            return LoadSettings(
                root,
                configFileName,
                machineWideSettings,
                loadUserWideSettings: true,
                useTestingGlobalPath: false);
        }

        /// <summary>
        /// Loads Specific NuGet.Config file. The method only loads specific config file 
        /// which is file <paramref name="configFileName"/>from <paramref name="root"/>.
        /// </summary>
        public static ISettings LoadSpecificSettings(string root, string configFileName)
        {
            if (string.IsNullOrEmpty(configFileName))
            {
                throw new ArgumentException(Resources.Argument_Cannot_Be_Null_Or_Empty, nameof(configFileName));
            }

            return LoadSettings(
                root,
                configFileName,
                machineWideSettings: null,
                loadUserWideSettings: true,
                useTestingGlobalPath: false);
        }

        public static ISettings LoadSettingsGivenConfigPaths(IList<string> configFilePaths)
        {
            var settings = new List<SettingsFile>();
            if (configFilePaths == null || configFilePaths.Count == 0)
            {
                return NullSettings.Instance;
            }

            foreach (var configFile in configFilePaths)
            {
                var file = new FileInfo(configFile);
                settings.Add(new SettingsFile(file.DirectoryName, file.Name));
            }

            return LoadSettingsForSpecificConfigs(
                settings.First().DirectoryPath,
                settings.First().FileName,
                validSettingFiles: settings,
                machineWideSettings: null,
                loadUserWideSettings: false,
                useTestingGlobalPath: false);
        }

        /// <summary>
        /// For internal use only
        /// </summary>
        internal static ISettings LoadSettings(
            string root,
            string configFileName,
            IMachineWideSettings machineWideSettings,
            bool loadUserWideSettings,
            bool useTestingGlobalPath)
        {
            // Walk up the tree to find a config file; also look in .nuget subdirectories
            // If a configFile is passed, don't walk up the tree. Only use that single config file.
            var validSettingFiles = new List<SettingsFile>();
            if (root != null && string.IsNullOrEmpty(configFileName))
            {
                validSettingFiles.AddRange(
                    GetSettingsFilesFullPath(root)
                        .Select(f => ReadSettings(root, f))
                        .Where(f => f != null));
            }

            return LoadSettingsForSpecificConfigs(
                root,
                configFileName,
                validSettingFiles,
                machineWideSettings,
                loadUserWideSettings,
                useTestingGlobalPath);
        }

        private static ISettings LoadSettingsForSpecificConfigs(
            string root,
            string configFileName,
            List<SettingsFile> validSettingFiles,
            IMachineWideSettings machineWideSettings,
            bool loadUserWideSettings,
            bool useTestingGlobalPath)
        {
            if (loadUserWideSettings)
            {
                var userSpecific = LoadUserSpecificSettings(root, configFileName, useTestingGlobalPath);
                if (userSpecific != null)
                {
                    validSettingFiles.Add(userSpecific);
                }
            }

            if (machineWideSettings != null && machineWideSettings.Settings is Settings mwSettings && string.IsNullOrEmpty(configFileName))
            {
                // Priority gives you the settings file in the order you want to start reading them
                validSettingFiles.AddRange(
                    mwSettings.Priority.Select(
                        s => new SettingsFile(s.DirectoryPath, s.FileName, s.IsMachineWide)));
            }

            if (validSettingFiles?.Any() != true)
            {
                // This means we've failed to load all config files and also failed to load or create the one in %AppData%
                // Work Item 1531: If the config file is malformed and the constructor throws, NuGet fails to load in VS.
                // Returning a null instance prevents us from silently failing and also from picking up the wrong config
                return NullSettings.Instance;
            }

            SettingsFile.ConnectSettingsFilesLinkedList(validSettingFiles);

            // Create a settings object with the linked list head. Typically, it's either the config file in %ProgramData%\NuGet\Config,
            // or the user wide config (%APPDATA%\NuGet\nuget.config) if there are no machine
            // wide config files. The head file is the one we want to read first, while the user wide config
            // is the one that we want to write to.
            // TODO: add UI to allow specifying which one to write to
            return new Settings(validSettingFiles.Last());
        }

        private static SettingsFile LoadUserSpecificSettings(
            string root,
            string configFileName,
            bool useTestingGlobalPath)
        {
            // Path.Combine is performed with root so it should not be null
            // However, it is legal for it be empty in this method
            var rootDirectory = root ?? string.Empty;

            // for the default location, allow case where file does not exist, in which case it'll end
            // up being created if needed
            SettingsFile userSpecificSettings = null;
            if (configFileName == null)
            {
                var defaultSettingsFilePath = string.Empty;
                if (useTestingGlobalPath)
                {
                    defaultSettingsFilePath = Path.Combine(rootDirectory, "TestingGlobalPath", DefaultSettingsFileName);
                }
                else
                {
                    var userSettingsDir = NuGetEnvironment.GetFolderPath(NuGetFolderPath.UserSettingsDirectory);

                    // If there is no user settings directory, return no settings
                    if (userSettingsDir == null)
                    {
                        return null;
                    }
                    defaultSettingsFilePath = Path.Combine(userSettingsDir, DefaultSettingsFileName);
                }

                userSpecificSettings = ReadSettings(rootDirectory, defaultSettingsFilePath);

                if (File.Exists(defaultSettingsFilePath) && userSpecificSettings.IsEmpty())
                {
                    var trackFilePath = Path.Combine(Path.GetDirectoryName(defaultSettingsFilePath), NuGetConstants.AddV3TrackFile);

                    if (!File.Exists(trackFilePath))
                    {
                        File.Create(trackFilePath).Dispose();

                        var defaultSource = new SourceItem(NuGetConstants.FeedName, NuGetConstants.V3FeedUrl, protocolVersion: "3");
                        userSpecificSettings.AddOrUpdate(ConfigurationConstants.PackageSources, defaultSource);
                        userSpecificSettings.SaveToDisk();
                    }
                }
            }
            else
            {
                if (!FileSystemUtility.DoesFileExistIn(rootDirectory, configFileName))
                {
                    var message = string.Format(CultureInfo.CurrentCulture,
                        Resources.FileDoesNotExist,
                        Path.Combine(rootDirectory, configFileName));
                    throw new InvalidOperationException(message);
                }

                userSpecificSettings = ReadSettings(rootDirectory, configFileName);
            }

            return userSpecificSettings;
        }

        /// <summary>
        /// Loads the machine wide settings.
        /// </summary>
        /// <remarks>
        /// For example, if <paramref name="paths" /> is {"IDE", "Version", "SKU" }, then
        /// the files loaded are (in the order that they are loaded):
        /// %programdata%\NuGet\Config\IDE\Version\SKU\*.config
        /// %programdata%\NuGet\Config\IDE\Version\*.config
        /// %programdata%\NuGet\Config\IDE\*.config
        /// %programdata%\NuGet\Config\*.config
        /// </remarks>
        /// <param name="root">The file system in which the settings files are read.</param>
        /// <param name="paths">The additional paths under which to look for settings files.</param>
        /// <returns>The list of settings read.</returns>
        public static ISettings LoadMachineWideSettings(
            string root,
            params string[] paths)
        {
            if (string.IsNullOrEmpty(root))
            {
                throw new ArgumentException("root cannot be null or empty");
            }

            var settingFiles = new List<SettingsFile>();
            var combinedPath = Path.Combine(paths);

            while (true)
            {
                // load setting files in directory
                foreach (var file in FileSystemUtility.GetFilesRelativeToRoot(root, combinedPath, SupportedMachineWideConfigExtension, SearchOption.TopDirectoryOnly))
                {
                    var settings = ReadSettings(root, file, isMachineWideSettings: true);
                    if (settings != null)
                    {
                        settingFiles.Add(settings);
                    }
                }

                if (combinedPath.Length == 0)
                {
                    break;
                }

                var index = combinedPath.LastIndexOf(Path.DirectorySeparatorChar);
                if (index < 0)
                {
                    index = 0;
                }
                combinedPath = combinedPath.Substring(0, index);
            }

            if (settingFiles.Any())
            {
                SettingsFile.ConnectSettingsFilesLinkedList(settingFiles);

                return new Settings(settingFiles.Last());
            }

            return NullSettings.Instance;
        }

        public static string ApplyEnvironmentTransform(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            return Environment.ExpandEnvironmentVariables(value);
        }

        public static Tuple<string, string> GetFileNameAndItsRoot(string root, string settingsPath)
        {
            string fileName = null;
            string directory = null;

            if (Path.IsPathRooted(settingsPath))
            {
                fileName = Path.GetFileName(settingsPath);
                directory = Path.GetDirectoryName(settingsPath);
            }
            else if (!FileSystemUtility.IsPathAFile(settingsPath))
            {
                var fullPath = Path.Combine(root ?? string.Empty, settingsPath);
                fileName = Path.GetFileName(fullPath);
                directory = Path.GetDirectoryName(fullPath);
            }
            else
            {
                fileName = settingsPath;
                directory = root;
            }

            return new Tuple<string, string>(fileName, directory);
        }

        internal static string ResolvePathFromOrigin(string originDirectoryPath, string originFilePath, string path)
        {
            if (Uri.TryCreate(path, UriKind.Relative, out var _) &&
                !string.IsNullOrEmpty(originDirectoryPath) &&
                !string.IsNullOrEmpty(originFilePath))
            {
                return ResolveRelativePath(originDirectoryPath, originFilePath, path);
            }

            return path;
        }

        private static string ResolveRelativePath(string originDirectoryPath, string originFilePath, string path)
        {
            if (string.IsNullOrEmpty(originDirectoryPath) || string.IsNullOrEmpty(originFilePath))
            {
                return null;
            }

            if (string.IsNullOrEmpty(path))
            {
                return path;
            }

            return Path.Combine(originDirectoryPath, ResolvePath(Path.GetDirectoryName(originFilePath), path));
        }

        private static string ResolvePath(string configDirectory, string value)
        {
            // Three cases for when Path.IsRooted(value) is true:
            // 1- C:\folder\file
            // 2- \\share\folder\file
            // 3- \folder\file
            // In the first two cases, we want to honor the fully qualified path
            // In the last case, we want to return X:\folder\file with X: drive where config file is located.
            // However, Path.Combine(path1, path2) always returns path2 when Path.IsRooted(path2) == true (which is current case)
            var root = Path.GetPathRoot(value);
            // this corresponds to 3rd case
            if (root != null
                && root.Length == 1
                && (root[0] == Path.DirectorySeparatorChar || value[0] == Path.AltDirectorySeparatorChar))
            {
                return Path.Combine(Path.GetPathRoot(configDirectory), value.Substring(1));
            }
            return Path.Combine(configDirectory, value);
        }

        private static SettingsFile ReadSettings(string settingsRoot, string settingsPath, bool isMachineWideSettings = false)
        {
            try
            {
                var tuple = GetFileNameAndItsRoot(settingsRoot, settingsPath);
                var filename = tuple.Item1;
                var root = tuple.Item2;
                return new SettingsFile(root, filename, isMachineWideSettings);
            }
            catch (XmlException)
            {
                return null;
            }
        }

        /// <remarks>
        /// Order is most significant (e.g. applied last) to least significant (applied first)
        /// ex:
        /// c:\someLocation\nuget.config
        /// c:\nuget.config
        /// </remarks>
        private static IEnumerable<string> GetSettingsFilesFullPath(string root)
        {
            // for dirs obtained by walking up the tree, only consider setting files that already exist.
            // otherwise we'd end up creating them.
            foreach (var dir in GetSettingsFilePaths(root))
            {
                var fileName = GetSettingsFileNameFromDir(dir);
                if (fileName != null)
                {
                    yield return fileName;
                }
            }

            yield break;
        }

        /// <summary>
        /// Checks for each possible casing of nuget.config in the directory. The first match is
        /// returned. If there are no nuget.config files null is returned.
        /// </summary>
        /// <remarks>For windows <see cref="OrderedSettingsFileNames"/> contains a single casing since
        /// the file system is case insensitive.</remarks>
        private static string GetSettingsFileNameFromDir(string directory)
        {
            foreach (var nugetConfigCasing in OrderedSettingsFileNames)
            {
                var file = Path.Combine(directory, nugetConfigCasing);
                if (File.Exists(file))
                {
                    return file;
                }
            }

            return null;
        }

        private static IEnumerable<string> GetSettingsFilePaths(string root)
        {
            while (root != null)
            {
                yield return root;
                root = Path.GetDirectoryName(root);
            }

            yield break;
        }

        // TODO: Delete obsolete methods https://github.com/NuGet/Home/issues/7294
#pragma warning disable CS0618 // Type or member is obsolete

        [Obsolete("GetValue(...) is deprecated. Please use GetSection(...) to interact with the setting values instead.")]
        public string GetValue(string section, string key, bool isPath = false)
        {
            if (string.IsNullOrEmpty(section))
            {
                throw new ArgumentException(Resources.Argument_Cannot_Be_Null_Or_Empty, nameof(section));
            }

            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException(Resources.Argument_Cannot_Be_Null_Or_Empty, nameof(key));
            }

            var sectionElement = GetSection(section);
            var item = sectionElement?.GetFirstItemWithAttribute<AddItem>(ConfigurationConstants.KeyAttribute, key);

            if (isPath)
            {
                return item?.GetValueAsPath();
            }

            return item?.Value;
        }

        [Obsolete("GetAllSubsections(...) is deprecated. Please use GetSection(...) to interact with the setting values instead.")]
        public IReadOnlyList<string> GetAllSubsections(string section)
        {
            if (string.IsNullOrEmpty(section))
            {
                throw new ArgumentException(Resources.Argument_Cannot_Be_Null_Or_Empty, nameof(section));
            }

            var sectionElement = GetSection(section);

            if (sectionElement == null)
            {
                return new List<string>().AsReadOnly();
            }

            return sectionElement.Items.Where(c => c is CredentialsItem || c is UnknownItem).Select(i => i.ElementName).ToList().AsReadOnly();
        }

        [Obsolete("GetSettingValues(...) is deprecated. Please use GetSection(...) to interact with the setting values instead.")]
        public IList<SettingValue> GetSettingValues(string section, bool isPath = false)
        {
            if (string.IsNullOrEmpty(section))
            {
                throw new ArgumentException(Resources.Argument_Cannot_Be_Null_Or_Empty, nameof(section));
            }

            var sectionElement = GetSection(section);

            if (sectionElement == null)
            {
                return new List<SettingValue>().AsReadOnly();
            }

            return sectionElement?.Items.Select(i =>
            {
                if (i is AddItem addItem)
                {
                    return TransformAddItem(addItem, isPath);
                }

                return null;
            }).Where(i => i != null).ToList().AsReadOnly();
        }

        [Obsolete("GetNestedValues(...) is deprecated. Please use GetSection(...) to interact with the setting values instead.")]
        public IList<KeyValuePair<string, string>> GetNestedValues(string section, string subSection)
        {
            if (string.IsNullOrEmpty(section))
            {
                throw new ArgumentException(Resources.Argument_Cannot_Be_Null_Or_Empty, nameof(section));
            }

            if (string.IsNullOrEmpty(subSection))
            {
                throw new ArgumentException(Resources.Argument_Cannot_Be_Null_Or_Empty, nameof(subSection));
            }

            var values = GetNestedSettingValues(section, subSection);

            return values.Select(v => new KeyValuePair<string, string>(v.Key, v.Value)).ToList().AsReadOnly();
        }

        [Obsolete("GetNestedSettingValues(...) is deprecated. Please use GetSection(...) to interact with the setting values instead.")]
        public IReadOnlyList<SettingValue> GetNestedSettingValues(string section, string subSection)
        {
            if (string.IsNullOrEmpty(section))
            {
                throw new ArgumentException(Resources.Argument_Cannot_Be_Null_Or_Empty, nameof(section));
            }

            if (string.IsNullOrEmpty(subSection))
            {
                throw new ArgumentException(Resources.Argument_Cannot_Be_Null_Or_Empty, nameof(subSection));
            }

            var sectionElement = GetSection(section);
            if (sectionElement == null)
            {
                return new List<SettingValue>().AsReadOnly();
            }

            return sectionElement.Items.SelectMany(i =>
            {
                var settingValues = new List<SettingValue>();

                if (string.Equals(i.ElementName, subSection, StringComparison.OrdinalIgnoreCase))
                {
                    if (i is CredentialsItem credentials)
                    {
                        settingValues.Add(new SettingValue(
                            ConfigurationConstants.UsernameToken,
                            credentials.Username,
                            origin: this,
                            isMachineWide: credentials.Origin?.IsMachineWide ?? false,
                            originalValue: credentials.Username,
                            priority: 0));

                        settingValues.Add(new SettingValue(
                            credentials.IsPasswordClearText ? ConfigurationConstants.ClearTextPasswordToken : ConfigurationConstants.PasswordToken,
                            credentials.Password,
                            origin: this,
                            isMachineWide: credentials.Origin?.IsMachineWide ?? false,
                            originalValue: credentials.Password,
                            priority: 0));

                        if (!string.IsNullOrEmpty(credentials.ValidAuthenticationTypes))
                        {
                            settingValues.Add(new SettingValue(
                                ConfigurationConstants.ValidAuthenticationTypesToken,
                                credentials.ValidAuthenticationTypes,
                                origin: this,
                                isMachineWide: credentials.Origin?.IsMachineWide ?? false,
                                originalValue: credentials.ValidAuthenticationTypes,
                                priority: 0));
                        }
                    }
                    else if (i is UnknownItem unknown)
                    {
                        if (unknown.Children != null && unknown.Children.Any())
                        {
                            settingValues.AddRange(unknown.Children.Where(c => c is AddItem).Select(item => TransformAddItem(item as AddItem)).ToList());
                        }
                    }
                }

                return settingValues;
            }).ToList().AsReadOnly();
        }

        [Obsolete("SetValue(...) is deprecated. Please use AddOrUpdate(...) to add an item to a section or interact directly with the SettingItem you want.")]
        public void SetValue(string section, string key, string value)
        {
            if (string.IsNullOrEmpty(section))
            {
                throw new ArgumentException(Resources.Argument_Cannot_Be_Null_Or_Empty, nameof(section));
            }

            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException(Resources.Argument_Cannot_Be_Null_Or_Empty, nameof(key));
            }

            var itemToAdd = new AddItem(key, value);
            if (string.Equals(section, ConfigurationConstants.PackageSources, StringComparison.OrdinalIgnoreCase))
            {
                itemToAdd = new SourceItem(key, value);
            }

            AddOrUpdate(section, itemToAdd);
            SaveToDisk();
        }

        [Obsolete("SetValues(...) is deprecated. Please use AddOrUpdate(...) to add an item to a section or interact directly with the SettingItem you want.")]
        public void SetValues(string section, IReadOnlyList<SettingValue> values)
        {
            if (string.IsNullOrEmpty(section))
            {
                throw new ArgumentException(Resources.Argument_Cannot_Be_Null_Or_Empty, nameof(section));
            }

            if (values == null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            foreach (var value in values)
            {
                AddOrUpdate(section, TransformSettingValue(section, value));
            }

            SaveToDisk();
        }

        [Obsolete("UpdateSections(...) is deprecated. Please use AddOrUpdate(...) to update an item in a section or interact directly with the SettingItem you want.")]
        public void UpdateSections(string section, IReadOnlyList<SettingValue> values)
        {
            if (string.IsNullOrEmpty(section))
            {
                throw new ArgumentException(Resources.Argument_Cannot_Be_Null_Or_Empty, nameof(section));
            }

            if (values == null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            foreach (var value in values)
            {
                AddOrUpdate(section, TransformSettingValue(section, value));
            }

            SaveToDisk();
        }

        [Obsolete("UpdateSubsections(...) is deprecated. Please use AddOrUpdate(...) to update an item in a section or interact directly with the SettingItem you want.")]
        public void UpdateSubsections(string section, string subsection, IReadOnlyList<SettingValue> values)
        {
            if (string.IsNullOrEmpty(section))
            {
                throw new ArgumentException(Resources.Argument_Cannot_Be_Null_Or_Empty, nameof(section));
            }

            if (string.IsNullOrEmpty(subsection))
            {
                throw new ArgumentException(Resources.Argument_Cannot_Be_Null_Or_Empty, nameof(subsection));
            }

            if (values == null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            if (values.Any())
            {
                SettingItem itemToAdd = null;

                if (string.Equals(ConfigurationConstants.CredentialsSectionName, section, StringComparison.OrdinalIgnoreCase) &&
                    GetCredentialsItemValues(values, out var username, out var password, out var isPasswordClearText, out var validAuthenticationTypes))
                {
                    itemToAdd = new CredentialsItem(subsection, username, password, isPasswordClearText, validAuthenticationTypes);
                }
                else
                {
                    itemToAdd = new UnknownItem(subsection, attributes: null, children: values.Select(v => TransformSettingValue(subsection, v)));
                }

                AddOrUpdate(section, itemToAdd);
            }
            else
            {
                try
                {
                    var sectionElement = GetSection(section);
                    var item = sectionElement?.Items.Where(c => string.Equals(c.ElementName, subsection, StringComparison.OrdinalIgnoreCase)).FirstOrDefault();

                    if (item != null)
                    {
                        Remove(section, item);
                    }
                } catch { }
            }

            SaveToDisk();
        }

        [Obsolete("SetNestedValues(...) is deprecated. Please use AddOrUpdate(...) to update an item in a section or interact directly with the SettingItem you want.")]
        public void SetNestedValues(string section, string subsection, IList<KeyValuePair<string, string>> values)
        {
            if (string.IsNullOrEmpty(section))
            {
                throw new ArgumentException(Resources.Argument_Cannot_Be_Null_Or_Empty, nameof(section));
            }

            if (string.IsNullOrEmpty(subsection))
            {
                throw new ArgumentException(Resources.Argument_Cannot_Be_Null_Or_Empty, nameof(subsection));
            }

            if (values == null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            SetNestedSettingValues(section, subsection, values.Select(kvp => new SettingValue(kvp.Key, kvp.Value, isMachineWide: false)).ToList());
        }

        [Obsolete("SetNestedSettingValues(...) is deprecated. Please use AddOrUpdate(...) to update an item in a section or interact directly with the SettingItem you want.")]
        public void SetNestedSettingValues(string section, string subsection, IList<SettingValue> values)
        {
            if (string.IsNullOrEmpty(section))
            {
                throw new ArgumentException(Resources.Argument_Cannot_Be_Null_Or_Empty, nameof(section));
            }

            if (string.IsNullOrEmpty(subsection))
            {
                throw new ArgumentException(Resources.Argument_Cannot_Be_Null_Or_Empty, nameof(subsection));
            }

            if (values == null)
            {
                throw new ArgumentNullException(nameof(values));
            }

            var updatedCurrentElement = false;

            if (_computedSections.TryGetValue(section, out var sectionElement) && values.Any())
            {
                var subsectionItem = sectionElement.Items.FirstOrDefault(c => string.Equals(c.ElementName, subsection, StringComparison.OrdinalIgnoreCase));

                if (subsectionItem != null)
                {
                    if (subsectionItem is CredentialsItem credential)
                    {
                        var updatedCredential = credential.Clone() as CredentialsItem;

                        GetCredentialsItemValues(values, out var username, out var password, out var isPasswordClearText, out var validAuthenticationTypes);
                        if (!string.IsNullOrEmpty(username))
                        {
                            updatedCredential.Username = username;
                        }

                        if (!string.IsNullOrEmpty(password))
                        {
                            updatedCredential.UpdatePassword(password, isPasswordClearText);
                        }

                        if (!string.IsNullOrEmpty(validAuthenticationTypes))
                        {
                            updatedCredential.ValidAuthenticationTypes = validAuthenticationTypes;
                        }

                        credential.Update(updatedCredential);

                        updatedCurrentElement = true;

                    }
                    else if (subsectionItem is UnknownItem unknown)
                    {
                        foreach (var value in values)
                        {
                            unknown.Add(TransformSettingValue(subsection, value));
                        }

                        updatedCurrentElement = true;
                    }
                }
            }

            if (!updatedCurrentElement)
            {
                var isItemUnknown = true;
                SettingItem item = null;


                if (string.Equals(section, ConfigurationConstants.CredentialsSectionName, StringComparison.OrdinalIgnoreCase) &&
                    GetCredentialsItemValues(values, out var username, out var password, out var isPasswordClearText, out var validAuthenticationTypes))
                {
                    isItemUnknown = false;
                    item = new CredentialsItem(subsection, username, password, isPasswordClearText, validAuthenticationTypes);
                }

                if (isItemUnknown)
                {
                    item = new UnknownItem(subsection, attributes: null, children: values.Select(v => TransformSettingValue(subsection, v)));
                }

                AddOrUpdate(section, item);
            }

            SaveToDisk();
        }

        private bool GetCredentialsItemValues(IEnumerable<SettingValue> values, out string username, out string password, out bool isPasswordClearText, out string validAuthenticationTypes)
        {
            username = string.Empty;
            password = string.Empty;
            isPasswordClearText = true;
            validAuthenticationTypes = string.Empty;

            foreach (var item in values)
            {
                if (string.Equals(item.Key, ConfigurationConstants.UsernameToken, StringComparison.OrdinalIgnoreCase))
                {
                    username = item.Value;
                }
                else if (string.Equals(item.Key, ConfigurationConstants.PasswordToken, StringComparison.OrdinalIgnoreCase))
                {
                    password = item.Value;
                    isPasswordClearText = false;
                }
                else if (string.Equals(item.Key, ConfigurationConstants.ClearTextPasswordToken, StringComparison.OrdinalIgnoreCase))
                {
                    password = item.Value;
                    isPasswordClearText = true;
                }
                else if (string.Equals(item.Key, ConfigurationConstants.ValidAuthenticationTypesToken, StringComparison.OrdinalIgnoreCase))
                {
                    validAuthenticationTypes = item.Value;
                }
            }

            return !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password);
        }

        [Obsolete("DeleteValue(...) is deprecated. Please use Remove(...) with the item you want to remove from the setttings.")]
        public bool DeleteValue(string section, string key)
        {
            if (string.IsNullOrEmpty(section))
            {
                throw new ArgumentException(Resources.Argument_Cannot_Be_Null_Or_Empty, nameof(section));
            }

            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException(Resources.Argument_Cannot_Be_Null_Or_Empty, nameof(key));
            }

            var sectionElement = GetSection(section);
            var item = sectionElement?.GetFirstItemWithAttribute<AddItem>(ConfigurationConstants.KeyAttribute, key);

            if (item != null)
            {
                try
                {
                    Remove(section, item);
                    SaveToDisk();

                    return true;
                }
                catch { }
            }

            return false;
        }

        [Obsolete("DeleteSection(...) is deprecated,. Please use Remove(...) with all the items in the section you want to remove from the setttings.")]
        public bool DeleteSection(string section)
        {
            if (string.IsNullOrEmpty(section))
            {
                throw new ArgumentException(Resources.Argument_Cannot_Be_Null_Or_Empty, nameof(section));
            }

            var sectionElement = GetSection(section);

            if (sectionElement != null)
            {
                var success = true;

                foreach (var item in sectionElement.Items)
                {
                    try
                    {
                        Remove(section, item);
                    }
                    catch
                    {
                        success = false;
                    }
                }

                SaveToDisk();

                return success;
            }

            return false;
        }

        private SettingValue TransformAddItem(AddItem addItem, bool isPath = false)
        {
            var value = isPath ? addItem.GetValueAsPath() : addItem.Value;
            var originalValue = addItem.Attributes[ConfigurationConstants.ValueAttribute];

            var settingValue = new SettingValue(addItem.Key, value, origin: this, isMachineWide: addItem.Origin?.IsMachineWide ?? false, originalValue: originalValue, priority: 0);

            foreach (var attribute in addItem.Attributes)
            {
                // Add all attributes other than ConfigurationContants.KeyAttribute and ConfigurationContants.ValueAttribute to AdditionalValues
                if (!string.Equals(attribute.Key, ConfigurationConstants.KeyAttribute, StringComparison.Ordinal) &&
                    !string.Equals(attribute.Key, ConfigurationConstants.ValueAttribute, StringComparison.Ordinal))
                {
                    settingValue.AdditionalData[attribute.Key] = attribute.Value;
                }
            }

            return settingValue;
        }

        private AddItem TransformSettingValue(string section, SettingValue value)
        {
            if (string.Equals(section, ConfigurationConstants.PackageSources, StringComparison.OrdinalIgnoreCase))
            {
                value.AdditionalData.TryGetValue(ConfigurationConstants.ProtocolVersionAttribute, out var protocol);
                return new SourceItem(value.Key, value.Value, protocol);
            }

            return new AddItem(value.Key, value.Value, new ReadOnlyDictionary<string, string>(value.AdditionalData));
        }

#pragma warning restore CS0618 // Type or member is obsolete
    }
}
