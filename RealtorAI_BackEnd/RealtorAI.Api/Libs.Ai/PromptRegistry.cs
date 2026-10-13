using System.Security.Cryptography;
using System.Text;

namespace Libs.Ai;

/// <summary>
/// A versioned prompt and the JSON Schema its output must satisfy (Principle VIII.5).
/// </summary>
/// <param name="TaskKey">The task this prompt serves.</param>
/// <param name="Version">Monotonic version number, from the filename.</param>
/// <param name="Text">The prompt text, exactly as stored on disk.</param>
/// <param name="OutputSchema">The JSON Schema document for the model's response.</param>
/// <param name="PromptHash">Lower-case hex SHA-256 of <paramref name="Text"/>.</param>
public sealed record PromptDefinition(
    string TaskKey,
    int Version,
    string Text,
    string OutputSchema,
    string PromptHash);

/// <summary>Reads the recorded prompt hash for a version. Implemented in the host.</summary>
public interface IPromptVersionStore
{
    /// <summary>
    /// Every recorded (task, version, hash) triple. Returned as a set rather than queried one
    /// at a time so the startup check is a single round trip.
    /// </summary>
    Task<IReadOnlyList<(string TaskKey, int Version, string PromptHash)>> GetAllAsync(
        CancellationToken cancellationToken);
}

/// <summary>Raised when the prompt files on disk disagree with what the database recorded.</summary>
public sealed class PromptDriftException : Exception
{
    public PromptDriftException(string message)
        : base(message)
    {
    }

    public PromptDriftException()
        : base("Prompt files have drifted from the recorded hashes.")
    {
    }

    public PromptDriftException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// T046 — the prompt registry (Principle VIII.5).
/// </summary>
public interface IPromptRegistry
{
    /// <summary>The active prompt for a task — the highest version present on disk.</summary>
    PromptDefinition GetActive(string taskKey);

    /// <summary>A specific version, for reproducing a past AI call from its audit record.</summary>
    PromptDefinition GetVersion(string taskKey, int version);

    /// <summary>Every prompt found on disk.</summary>
    IReadOnlyList<PromptDefinition> All();

    /// <summary>
    /// Compares every on-disk prompt against the hash recorded for its version, and throws on
    /// any disagreement. Called at startup.
    /// </summary>
    Task VerifyAgainstRecordedHashesAsync(
        IPromptVersionStore store,
        CancellationToken cancellationToken);
}

/// <summary>
/// Loads versioned prompts from <c>prompts/{task}/v{n}.md</c> with
/// <c>prompts/{task}/v{n}.schema.json</c> alongside, and verifies them against the hashes
/// recorded in <c>prompt_versions</c>.
///
/// <para>
/// <b>Why prompts are files with hashes rather than rows of text.</b> Every AI-derived value in
/// this system is auditable back to the prompt that produced it (FR-049), and an audit record
/// citing "listing.content v2" is only meaningful if v2 still means what it meant. A prompt is
/// editable text, so without a hash the citation silently decays: someone improves the wording,
/// and every past record now points at a prompt that never ran. Recording the hash at the time
/// the version was registered, and checking it at startup, makes that decay impossible to do
/// accidentally — editing a prompt means registering a new version.
/// </para>
/// <para>
/// <b>Why it fails at startup rather than per call.</b> Drift is a correctness problem for
/// records already written, not only for the next call. Refusing to start makes it a deployment
/// failure someone sees immediately; checking per call would let a drifted prompt serve traffic
/// until the first request happened to hit that task.
/// </para>
/// </summary>
public sealed class PromptRegistry : IPromptRegistry
{
    private readonly Dictionary<string, Dictionary<int, PromptDefinition>> prompts;

    private PromptRegistry(Dictionary<string, Dictionary<int, PromptDefinition>> prompts)
    {
        this.prompts = prompts;
    }

    /// <summary>
    /// Loads every prompt under <paramref name="promptsDirectory"/>.
    /// </summary>
    /// <exception cref="PromptDriftException">
    /// A prompt is missing its schema, or a filename is not parseable as a version. Both are
    /// treated as drift: a prompt whose output shape is undeclared cannot be validated, and
    /// FR-015 requires validation.
    /// </exception>
    public static PromptRegistry Load(string promptsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(promptsDirectory);

        var loaded = new Dictionary<string, Dictionary<int, PromptDefinition>>(StringComparer.Ordinal);

        if (!Directory.Exists(promptsDirectory))
        {
            // An empty registry is valid: no model is enabled yet, so nothing dispatches. It
            // becomes an error only when a task is actually requested, which GetActive reports
            // with the directory it looked in.
            return new PromptRegistry(loaded);
        }

        foreach (var taskDirectory in Directory.EnumerateDirectories(promptsDirectory))
        {
            var taskKey = Path.GetFileName(taskDirectory);

            var versions = new Dictionary<int, PromptDefinition>();

            foreach (var promptFile in Directory.EnumerateFiles(taskDirectory, "v*.md"))
            {
                var name = Path.GetFileNameWithoutExtension(promptFile);

                if (!TryParseVersion(name, out var version))
                {
                    throw new PromptDriftException(
                        $"'{promptFile}' is not a valid prompt filename. Expected v{{n}}.md, "
                        + "for example v1.md — the version in the filename is what an audit "
                        + "record cites.");
                }

                var schemaFile = Path.Combine(taskDirectory, $"v{version}.schema.json");

                if (!File.Exists(schemaFile))
                {
                    throw new PromptDriftException(
                        $"'{promptFile}' has no schema at '{schemaFile}'. FR-015 requires every "
                        + "model response to be validated against a declared schema, so a "
                        + "prompt without one cannot be dispatched.");
                }

                var text = File.ReadAllText(promptFile);

                versions[version] = new PromptDefinition(
                    taskKey,
                    version,
                    text,
                    File.ReadAllText(schemaFile),
                    ComputeHash(text));
            }

            if (versions.Count > 0)
            {
                loaded[taskKey] = versions;
            }
        }

        return new PromptRegistry(loaded);
    }

    public PromptDefinition GetActive(string taskKey)
    {
        AiTaskKey.EnsureKnown(taskKey);

        if (!prompts.TryGetValue(taskKey, out var versions) || versions.Count == 0)
        {
            throw new ModelUnavailableException(
                taskKey,
                $"no prompt is present. Expected at least prompts/{taskKey}/v1.md with "
                + $"prompts/{taskKey}/v1.schema.json alongside it.");
        }

        return versions[versions.Keys.Max()];
    }

    public PromptDefinition GetVersion(string taskKey, int version)
    {
        AiTaskKey.EnsureKnown(taskKey);

        if (!prompts.TryGetValue(taskKey, out var versions)
            || !versions.TryGetValue(version, out var definition))
        {
            throw new ModelUnavailableException(
                taskKey,
                $"prompt version {version} is not present on disk. An audit record citing it "
                + "can no longer be reproduced, which means a prompt file was deleted rather "
                + "than superseded.");
        }

        return definition;
    }

    public IReadOnlyList<PromptDefinition> All() =>
        prompts.Values.SelectMany(versions => versions.Values).ToList();

    public async Task VerifyAgainstRecordedHashesAsync(
        IPromptVersionStore store,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);

        var recorded = await store.GetAllAsync(cancellationToken);

        var recordedByKey = recorded.ToDictionary(
            row => (row.TaskKey, row.Version),
            row => row.PromptHash);

        var problems = new List<string>();

        foreach (var prompt in All())
        {
            if (!recordedByKey.TryGetValue((prompt.TaskKey, prompt.Version), out var recordedHash))
            {
                // Unregistered is not drift. A new prompt version exists on disk before anyone
                // records it, and registering it is a deliberate act. Dispatching to it is what
                // must be blocked, and AiService does that by requiring a recorded version.
                continue;
            }

            if (!string.Equals(recordedHash, prompt.PromptHash, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(
                    $"{prompt.TaskKey} v{prompt.Version}: recorded hash {recordedHash} but the "
                    + $"file on disk hashes to {prompt.PromptHash}");
            }
        }

        // The other direction: a recorded version whose file is gone. Past audit records cite
        // it, so it cannot be reproduced and the deletion was a mistake.
        foreach (var ((taskKey, version), _) in recordedByKey)
        {
            if (!prompts.TryGetValue(taskKey, out var versions) || !versions.ContainsKey(version))
            {
                problems.Add(
                    $"{taskKey} v{version} is recorded in prompt_versions but the file is "
                    + "missing. Audit records citing it can no longer be reproduced.");
            }
        }

        if (problems.Count > 0)
        {
            throw new PromptDriftException(
                "Prompt hash drift detected (Principle VIII.5). Every audit record citing an "
                + "affected version now points at text that did not produce it. Register a new "
                + "version instead of editing a released one.\n  "
                + string.Join("\n  ", problems));
        }
    }

    private static bool TryParseVersion(string fileNameWithoutExtension, out int version)
    {
        version = 0;

        return fileNameWithoutExtension.Length > 1
            && (fileNameWithoutExtension[0] == 'v' || fileNameWithoutExtension[0] == 'V')
            && int.TryParse(
                fileNameWithoutExtension[1..],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out version)
            && version > 0;
    }

    /// <summary>
    /// Hashes the prompt with its line endings normalised to <c>\n</c>.
    ///
    /// <para>
    /// Without normalisation the same prompt hashes differently depending on whether git
    /// checked it out with CRLF or LF — this repository has <c>core.autocrlf</c> active, so
    /// that difference is real — and startup would fail on a developer machine while passing in
    /// CI, for a file nobody edited.
    /// </para>
    /// </summary>
    internal static string ComputeHash(string promptText)
    {
        var normalised = promptText.Replace("\r\n", "\n", StringComparison.Ordinal);

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalised)));
    }
}
