#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Shoots.UI.Blueprints;

namespace Shoots.UI.ViewModels;

public sealed class BlueprintEntryViewModel : INotifyPropertyChanged
{
    private const string LineSep = "\n";

    private readonly Action<BlueprintEntryViewModel> _onSaveRequested;
    private SystemBlueprint _lastSaved;
    private readonly DateTimeOffset _createdUtc;

    private string _name;
    private string _description;
    private string _intentsText;
    private string _artifactsText;
    private string _version;
    private string _definitionText;

    private string _validationSummary = string.Empty;
    private bool _isValid = true;
    private bool _isDirty;

    private bool _isApplyingSnapshot;

    public BlueprintEntryViewModel(SystemBlueprint blueprint, Action<BlueprintEntryViewModel> onSaveRequested)
    {
        if (blueprint is null) throw new ArgumentNullException(nameof(blueprint));

        _onSaveRequested = onSaveRequested ?? throw new ArgumentNullException(nameof(onSaveRequested));
        _lastSaved = blueprint;

        _createdUtc = blueprint.CreatedUtc;

        _name = blueprint.Name ?? string.Empty;
        _description = blueprint.Description ?? string.Empty;
        _intentsText = JoinLines(blueprint.Intents);
        _artifactsText = JoinLines(blueprint.Artifacts);
        _version = blueprint.Version ?? string.Empty;
        _definitionText = blueprint.Definition ?? string.Empty;

        Validate();
        UpdateDirtyState();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name
    {
        get => _name;
        set => SetAndRevalidate(ref _name, value, nameof(Name));
    }

    public string Description
    {
        get => _description;
        set => SetAndRevalidate(ref _description, value, nameof(Description));
    }

    public string IntentsText
    {
        get => _intentsText;
        set => SetAndRevalidate(ref _intentsText, value, nameof(IntentsText));
    }

    public string ArtifactsText
    {
        get => _artifactsText;
        set => SetAndRevalidate(ref _artifactsText, value, nameof(ArtifactsText));
    }

    public string Version
    {
        get => _version;
        set => SetAndRevalidate(ref _version, value, nameof(Version));
    }

    public string DefinitionText
    {
        get => _definitionText;
        set => SetAndRevalidate(ref _definitionText, value, nameof(DefinitionText));
    }

    public string CreatedUtc => _createdUtc.ToString("u");

    public bool IsValid
    {
        get => _isValid;
        private set
        {
            if (_isValid == value) return;
            _isValid = value;
            OnPropertyChanged(nameof(IsValid));
            OnPropertyChanged(nameof(HasValidationErrors));
            OnPropertyChanged(nameof(CanSave));
        }
    }

    public bool HasValidationErrors => !IsValid;

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (_isDirty == value) return;
            _isDirty = value;
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(DirtyStateLabel));
            OnPropertyChanged(nameof(CanSave));
            OnPropertyChanged(nameof(CanRevert));
        }
    }

    public string DirtyStateLabel => IsDirty ? "Unsaved changes" : "Saved";

    public bool CanSave => IsDirty && IsValid;

    public bool CanRevert => IsDirty;

    public string ValidationSummary
    {
        get => _validationSummary;
        private set
        {
            if (_validationSummary == value) return;
            _validationSummary = value;
            OnPropertyChanged(nameof(ValidationSummary));
        }
    }

    public SystemBlueprint ToBlueprint()
        => new SystemBlueprint(
            Name: (Name ?? string.Empty).Trim(),
            Description: (Description ?? string.Empty).Trim(),
            Intents: ParseLines(IntentsText),
            Artifacts: ParseLines(ArtifactsText),
            Version: (Version ?? string.Empty).Trim(),
            Definition: (DefinitionText ?? string.Empty).Trim(),
            CreatedUtc: _createdUtc);

    public bool TrySave()
    {
        Validate();
        if (!IsValid)
            return false;

        _lastSaved = ToBlueprint();
        UpdateDirtyState();
        _onSaveRequested(this);
        return true;
    }

    public void RevertToLastSaved()
    {
        ApplySnapshot(_lastSaved);
        Validate();
        UpdateDirtyState();
    }

    private void ApplySnapshot(SystemBlueprint blueprint)
    {
        _isApplyingSnapshot = true;
        try
        {
            // Set fields directly to avoid triggering setters repeatedly.
            _name = blueprint.Name ?? string.Empty;
            _description = blueprint.Description ?? string.Empty;
            _intentsText = JoinLines(blueprint.Intents);
            _artifactsText = JoinLines(blueprint.Artifacts);
            _version = blueprint.Version ?? string.Empty;
            _definitionText = blueprint.Definition ?? string.Empty;

            // Notify UI once per property.
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(IntentsText));
            OnPropertyChanged(nameof(ArtifactsText));
            OnPropertyChanged(nameof(Version));
            OnPropertyChanged(nameof(DefinitionText));
        }
        finally
        {
            _isApplyingSnapshot = false;
        }
    }

    private void SetAndRevalidate(ref string field, string? value, string propertyName)
    {
        var next = value ?? string.Empty;
        if (field == next) return;

        field = next;
        OnPropertyChanged(propertyName);

        if (_isApplyingSnapshot)
            return;

        Validate();
        UpdateDirtyState();
    }

    private void Validate()
    {
        var issues = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
            issues.Add("Name is empty.");

        if (ParseLines(IntentsText).Count == 0)
            issues.Add("Add at least one intent line.");

        if (ParseLines(ArtifactsText).Count == 0)
            issues.Add("Add at least one artifact line.");

        if (string.IsNullOrWhiteSpace(Version))
            issues.Add("Version is empty.");

        var defIssue = ValidateDefinition(DefinitionText);
        if (!string.IsNullOrWhiteSpace(defIssue))
            issues.Add(defIssue);

        ValidationSummary = issues.Count == 0 ? "No validation notes." : string.Join(" ", issues);
        IsValid = issues.Count == 0;
    }

    private static string JoinLines(IReadOnlyList<string>? lines)
    {
        if (lines is null || lines.Count == 0)
            return string.Empty;

        // UI text areas do fine with '\n'. Also avoids Environment/NewLine namespace ambiguity.
        return string.Join(LineSep, lines.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()));
    }

    private static IReadOnlyList<string> ParseLines(string value)
        => (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split(new[] { "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

    private static string? ValidateDefinition(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Blueprint definition is empty.";

        var trimmed = value.Trim();

        // JSON?
        if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                _ = System.Text.Json.JsonDocument.Parse(trimmed);
                return null;
            }
            catch (System.Text.Json.JsonException ex)
            {
                return $"Definition JSON is invalid: {ex.Message}";
            }
        }

        // YAML-ish heuristic (good enough for UI validation)
        var lines = trimmed
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split(new[] { "\n" }, StringSplitOptions.RemoveEmptyEntries);

        var hasYamlTokens =
            lines.Any(line =>
                line.Contains(":", StringComparison.Ordinal) ||
                line.TrimStart().StartsWith("-", StringComparison.Ordinal));

        return hasYamlTokens ? null : "Definition is neither valid JSON nor recognizable YAML.";
    }

    private void UpdateDirtyState()
    {
        var current = ToBlueprint();
        IsDirty = !Equals(current, _lastSaved);
    }

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}