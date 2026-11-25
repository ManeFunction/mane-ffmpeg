using System.Xml.Linq;
using ManeFfmpeg.Core.Model;
using ManeFfmpeg.Core.Persistence;

namespace ManeFfmpeg.Core.Platforms.Mac;

/// <summary>
/// Finder Quick Actions as Automator workflows (what Automator saves for a "Quick Action" that receives movie files in
/// Finder): one "Run Shell Script" action that hands the selected files to the app. The workflow only holds the preset's
/// id, so the app runs the preset as it is when the action is chosen; a rename rewrites the workflow under the new name.
/// </summary>
/// <param name="servicesDirectory">~/Library/Services.</param>
/// <param name="commandFor">The shell line that starts the app for a preset id; the selected files are in "$@".</param>
/// <param name="refresh">Called after a change, to make the system read the folder again.</param>
/// <param name="servicesMenu">
/// The system's settings for the menu items (<see cref="FinderServices"/>): a workflow that only lies in the folder is
/// disabled, and listed alphabetically after the ordered Quick Actions. New ones are enabled and go last, so the order
/// is the order they were added in.
/// </param>
public sealed class MacQuickActions(string servicesDirectory, Func<string, string> commandFor, Action refresh,
    IServicesMenu servicesMenu) : IQuickActions
{
    /// <summary>The Info.plist key that marks a workflow as this app's and names its preset.</summary>
    internal const string PresetKey = "ManeFfmpegPresetId";

    public bool IsInstalled(string presetId) => WorkflowsOf(presetId).Any();

    public void Install(Preset preset)
    {
        var existing = WorkflowsOf(preset.Id).ToList();
        var previousName = existing.Select(MenuNameOf).FirstOrDefault(n => n is not null);
        var (path, name) = FreePath(MenuName(preset.Name), existing);
        var contents = Path.Combine(path, "Contents");
        Directory.CreateDirectory(contents);
        var icon = IconFor(preset.Kind);
        // Each file is replaced whole, and the old workflow only goes once the new one is there: a failed write
        // leaves the preset with a Quick Action.
        AtomicFile.WriteAllText(Path.Combine(contents, "document.wflow"), Document(Script(preset), icon));
        AtomicFile.WriteAllText(Path.Combine(contents, "Info.plist"), InfoPlist(name, preset.Id, icon));
        foreach (var old in existing.Where(p => p != path))
            Directory.Delete(old, recursive: true);
        AppLog.Write($"Quick Action “{name}” saved: {path}");
        servicesMenu.Added(name, previousName);
        refresh();
    }

    public void Remove(string presetId)
    {
        var removed = false;
        foreach (var path in WorkflowsOf(presetId).ToList())
        {
            var name = MenuNameOf(path);
            Directory.Delete(path, recursive: true);
            if (name is not null)
                servicesMenu.Removed(name);
            AppLog.Write($"Quick Action removed: {path}");
            removed = true;
        }
        if (removed)
            refresh();
    }

    /// <summary>The workflows this app made for <paramref name="presetId"/> (normally one).</summary>
    private IEnumerable<string> WorkflowsOf(string presetId) =>
        Directory.Exists(servicesDirectory)
            ? Directory.EnumerateDirectories(servicesDirectory, "*.workflow").Where(p => PresetIdOf(p) == presetId)
            : [];

    /// <summary>The menu item's name in a workflow's Info.plist.</summary>
    private static string? MenuNameOf(string workflow) =>
        InfoOf(workflow) is { } info && Plist.Get(info, "NSServices") is { } services
            ? services.Elements("dict").Select(s => Plist.Get(s, "NSMenuItem")).OfType<XElement>()
                .Select(item => Plist.Get(item, "default")?.Value).FirstOrDefault(name => name is not null)
            : null;

    internal static string? PresetIdOf(string workflow) => InfoOf(workflow) is { } info ? Plist.Get(info, PresetKey)?.Value : null;

    private static XElement? InfoOf(string workflow) => Plist.Load(Path.Combine(workflow, "Contents", "Info.plist"));

    /// <summary>
    /// The menu item: the preset's name on one line. Finder shows a ":" in a file name as "/" (and the other way round),
    /// so a ":" can't be shown and becomes "-".
    /// </summary>
    internal static string MenuName(string presetName)
    {
        var name = string.Join(" ", presetName.Split((char[])['\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
            .Replace(':', '-').Trim().TrimStart('.');
        return name.Length > 0 ? name : "Mane FFmpeg";
    }

    /// <summary>The workflow's file name for a menu name: a "/" is stored as ":", which Finder shows as "/".</summary>
    private static string FileName(string menuName) => menuName.Replace('/', ':');

    /// <summary>
    /// "&lt;name&gt;.workflow" and the menu item "&lt;name&gt;", unless another workflow (the user's own, or another preset's)
    /// has that name already: then "&lt;name&gt; (Mane FFmpeg)", numbered if needed. The menu item is renamed too, because
    /// Finder keeps its settings by the menu item's name: two items with one name would share them, and removing one
    /// would turn the other off. The preset's own workflow can be reused.
    /// </summary>
    private (string Path, string MenuName) FreePath(string name, IReadOnlyCollection<string> own)
    {
        for (var n = 1; ; n++)
        {
            var menuName = n switch { 1 => name, 2 => $"{name} (Mane FFmpeg)", _ => $"{name} (Mane FFmpeg {n - 1})" };
            var candidate = Path.Combine(servicesDirectory, FileName(menuName) + ".workflow");
            if (!Directory.Exists(candidate) && !File.Exists(candidate) || own.Contains(candidate))
                return (candidate, menuName);
        }
    }

    internal string Script(Preset preset) =>
        $"# Mane FFmpeg: converts the selected videos with the preset “{preset.Name.ReplaceLineEndings(" ")}”, next to them.\n" +
        "# Made by Mane FFmpeg's presets window; remove it there.\n" +
        commandFor(preset.Id) + "\n";

    /// <summary>Escapes element text: only &amp;, &lt; and &gt; need it, so the script stays readable in the file.</summary>
    private static string X(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>
    /// The menu icon: a system image name, as Automator's icon picker writes it. A video camera for video presets, a
    /// speaker for audio ones (the system images have no music note).
    /// </summary>
    internal static string IconFor(PresetKind kind) =>
        kind == PresetKind.Audio ? "NSTouchBarAudioOutputVolumeHigh" : "NSTouchBarCommunicationVideo";

    private static string InfoPlist(string name, string presetId, string icon) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
        	<key>{PresetKey}</key>
        	<string>{X(presetId)}</string>
        	<key>NSServices</key>
        	<array>
        		<dict>
        			<key>NSBackgroundColorName</key>
        			<string>background</string>
        			<key>NSIconName</key>
        			<string>{icon}</string>
        			<key>NSMenuItem</key>
        			<dict>
        				<key>default</key>
        				<string>{X(name)}</string>
        			</dict>
        			<key>NSMessage</key>
        			<string>runWorkflowAsService</string>
        			<key>NSRequiredContext</key>
        			<dict>
        				<key>NSApplicationIdentifier</key>
        				<string>com.apple.finder</string>
        			</dict>
        			<key>NSSendFileTypes</key>
        			<array>
        				<string>public.movie</string>
        			</array>
        		</dict>
        	</array>
        </dict>
        </plist>

        """;

    private static string Uuid() => Guid.NewGuid().ToString().ToUpperInvariant();

    private static string Argument(int index, string name, string defaultValue) => $"""
        					<key>{index}</key>
        					<dict>
        						<key>default value</key>
        						{defaultValue}
        						<key>name</key>
        						<string>{name}</string>
        						<key>required</key>
        						<string>0</string>
        						<key>type</key>
        						<string>0</string>
        						<key>uuid</key>
        						<string>{index}</string>
        					</dict>
        """;

    /// <summary>The workflow: a Quick Action for movie files in Finder running one zsh script with the files as arguments.</summary>
    private static string Document(string script, string icon) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
        	<key>AMApplicationBuild</key>
        	<string>521.3</string>
        	<key>AMApplicationVersion</key>
        	<string>2.10</string>
        	<key>AMDocumentVersion</key>
        	<string>2</string>
        	<key>actions</key>
        	<array>
        		<dict>
        			<key>action</key>
        			<dict>
        				<key>AMAccepts</key>
        				<dict>
        					<key>Container</key>
        					<string>List</string>
        					<key>Optional</key>
        					<true/>
        					<key>Types</key>
        					<array>
        						<string>com.apple.cocoa.string</string>
        					</array>
        				</dict>
        				<key>AMActionVersion</key>
        				<string>2.0.3</string>
        				<key>AMApplication</key>
        				<array>
        					<string>Automator</string>
        				</array>
        				<key>AMParameterProperties</key>
        				<dict>
        					<key>COMMAND_STRING</key>
        					<dict/>
        					<key>CheckedForUserDefaultShell</key>
        					<dict/>
        					<key>inputMethod</key>
        					<dict/>
        					<key>shell</key>
        					<dict/>
        					<key>source</key>
        					<dict/>
        				</dict>
        				<key>AMProvides</key>
        				<dict>
        					<key>Container</key>
        					<string>List</string>
        					<key>Types</key>
        					<array>
        						<string>com.apple.cocoa.string</string>
        					</array>
        				</dict>
        				<key>ActionBundlePath</key>
        				<string>/System/Library/Automator/Run Shell Script.action</string>
        				<key>ActionName</key>
        				<string>Run Shell Script</string>
        				<key>ActionParameters</key>
        				<dict>
        					<key>COMMAND_STRING</key>
        					<string>{X(script)}</string>
        					<key>CheckedForUserDefaultShell</key>
        					<true/>
        					<key>inputMethod</key>
        					<integer>1</integer>
        					<key>shell</key>
        					<string>/bin/zsh</string>
        					<key>source</key>
        					<string></string>
        				</dict>
        				<key>BundleIdentifier</key>
        				<string>com.apple.RunShellScript</string>
        				<key>CFBundleVersion</key>
        				<string>2.0.3</string>
        				<key>CanShowSelectedItemsWhenRun</key>
        				<false/>
        				<key>CanShowWhenRun</key>
        				<true/>
        				<key>Category</key>
        				<array>
        					<string>AMCategoryUtilities</string>
        				</array>
        				<key>Class Name</key>
        				<string>RunShellScriptAction</string>
        				<key>InputUUID</key>
        				<string>{Uuid()}</string>
        				<key>Keywords</key>
        				<array>
        					<string>Shell</string>
        					<string>Script</string>
        					<string>Command</string>
        					<string>Run</string>
        					<string>Unix</string>
        				</array>
        				<key>OutputUUID</key>
        				<string>{Uuid()}</string>
        				<key>UUID</key>
        				<string>{Uuid()}</string>
        				<key>UnlocalizedApplications</key>
        				<array>
        					<string>Automator</string>
        				</array>
        				<key>arguments</key>
        				<dict>
        {Argument(0, "inputMethod", "<integer>0</integer>")}
        {Argument(1, "CheckedForUserDefaultShell", "<false/>")}
        {Argument(2, "source", "<string></string>")}
        {Argument(3, "COMMAND_STRING", "<string></string>")}
        {Argument(4, "shell", "<string>/bin/sh</string>")}
        				</dict>
        				<key>conversionLabel</key>
        				<integer>0</integer>
        				<key>isViewVisible</key>
        				<integer>1</integer>
        				<key>location</key>
        				<string>309.000000:305.000000</string>
        				<key>nibPath</key>
        				<string>/System/Library/Automator/Run Shell Script.action/Contents/Resources/Base.lproj/main.nib</string>
        			</dict>
        			<key>isViewVisible</key>
        			<integer>1</integer>
        		</dict>
        	</array>
        	<key>connectors</key>
        	<dict/>
        	<key>workflowMetaData</key>
        	<dict>
        		<key>applicationBundleID</key>
        		<string>com.apple.finder</string>
        		<key>applicationBundleIDsByPath</key>
        		<dict>
        			<key>/System/Library/CoreServices/Finder.app</key>
        			<string>com.apple.finder</string>
        		</dict>
        		<key>applicationPath</key>
        		<string>/System/Library/CoreServices/Finder.app</string>
        		<key>applicationPaths</key>
        		<array>
        			<string>/System/Library/CoreServices/Finder.app</string>
        		</array>
        		<key>inputTypeIdentifier</key>
        		<string>com.apple.Automator.fileSystemObject.movie</string>
        		<key>outputTypeIdentifier</key>
        		<string>com.apple.Automator.nothing</string>
        		<key>presentationMode</key>
        		<integer>15</integer>
        		<key>processesInput</key>
        		<integer>0</integer>
        		<key>serviceApplicationBundleID</key>
        		<string>com.apple.finder</string>
        		<key>serviceApplicationPath</key>
        		<string>/System/Library/CoreServices/Finder.app</string>
        		<key>serviceInputTypeIdentifier</key>
        		<string>com.apple.Automator.fileSystemObject.movie</string>
        		<key>serviceOutputTypeIdentifier</key>
        		<string>com.apple.Automator.nothing</string>
        		<key>serviceProcessesInput</key>
        		<integer>0</integer>
        		<key>systemImageName</key>
        		<string>{icon}</string>
        		<key>useAutomaticInputType</key>
        		<integer>0</integer>
        		<key>workflowTypeIdentifier</key>
        		<string>com.apple.Automator.servicesMenu</string>
        	</dict>
        </dict>
        </plist>

        """;
}
