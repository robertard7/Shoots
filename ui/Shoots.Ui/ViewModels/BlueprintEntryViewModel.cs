using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Shoots.UI.Blueprints;

namespace Shoots.UI.ViewModels;

public sealed class BlueprintEntryViewModel : INotifyPropertyChanged
{
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

    public BlueprintEntryViewModel(SystemBlueprint blueprint, Action<BlueprintEntryViewModel> onSaveRequested)
    {
        _onSaveRequested = onSaveRequested ?? throw new ArgumentNullException(nameof(onSaveRequested));
        _lastSaved = blueprint;
        _createdUtc = blueprint.CreatedUtc;
        _name = blueprint.Name;
        _description = blueprint.Description;
        _intentsText = string.Join(System.Environment.NewLine, blueprint.Intents);
		_artifactsText = string.Join(System.Environment.NewLine, blueprint.Artifacts);
        _version = blueprint.Version;
        _definitionText = blueprint.Definition;
        Validate();
        UpdateDirtyState();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
                return;

            _name = value;
            OnPropertyChanged(nameof(Name));
            ValidateAndSave();
        }
    }

    public string Description
    {
        get => _description;
        set
        {
            if (_description == value)
                return;

            _description = value;
            OnPropertyChanged(nameof(Description));
            ValidateAndSave();
        }
    }

    public string IntentsText
    {
        get => _intentsText;
        set
        {
            if (_intentsText == value)
                return;

            _intentsText = value;
            OnPropertyChanged(nameof(IntentsText));
            ValidateAndSave();
        }
    }

    public string ArtifactsText
    {
        get => _artifactsText;
        set
        {
            if (_artifactsText == value)
                return;

            _artifactsText = value;
            OnPropertyChanged(nameof(ArtifactsText));
            ValidateAndUpdate();
        }
    }

    public string Version
    {
        get => _version;
        set
        {
            if (_version == value)
                return;

            _version = value;
            OnPropertyChanged(nameof(Version));
            ValidateAndUpdate();
        }
    }

    public string DefinitionText
    {
        get => _definitionText;
        set
        {
            if (_definitionText == value)
                return;

            _definitionText = value;
            OnPropertyChanged(nameof(DefinitionText));
            ValidateAndUpdate();
        }
    }

    public string CreatedUtc => _createdUtc.ToString("u");

    public bool IsValid
    {
        get => _isValid;
        private set
        {
            if (_isValid == value)
                return;

            _isValid = value;
            OnPropertyChanged(nameof(IsValid));
            OnPropertyChanged(nameof(HasValidationErrors));
        }
    }

    public bool HasValidationErrors => !IsValid;

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (_isDirty == value)
                return;

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
            if (_validationSummary == value)
                return;

            _validationSummary = value;
            OnPropertyChanged(nameof(ValidationSummary));
        }
    }

	public SystemBlueprint ToBlueprint()
		=> new SystemBlueprint(
			Name: Name.Trim(),
			Description: Description.Trim(),
			Intents: ParseLines(IntentsText),
			Artifacts: ParseLines(ArtifactsText),
			Version: Version.Trim(),
			Definition: DefinitionText.Trim(),
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
        Name = _lastSaved.Name;
        Description = _lastSaved.Description;
        IntentsText = string.Join(System.Environment.NewLine, _lastSaved.Intents);
		ArtifactsText = string.Join(System.Environment.NewLine, _lastSaved.Artifacts);
        Version = _lastSaved.Version;
        DefinitionText = _lastSaved.Definition;
        Validate();
        UpdateDirtyState();
    }

    private void ValidateAndUpdate()
    {
        Validate();
        UpdateDirtyState();
    }
	
	private void ValidateAndSave()
	{
		Validate();
		UpdateDirtyState();
	}

    private void Validate()
    {
        var issues = new[]
            {
                string.IsNullOrWhiteSpace(Name) ? "Name is empty." : null,
                ParseLines(IntentsText).Count == 0 ? "Add at least one intent line." : null,
                ParseLines(ArtifactsText).Count == 0 ? "Add at least one artifact line." : null,
                string.IsNullOrWhiteSpace(Version) ? "Version is empty." : null,
                ValidateDefinition(DefinitionText)
            }
            .Where(issue => !string.IsNullOrWhiteSpace(issue))
            .ToList();

        ValidationSummary = issues.Count == 0 ? "No validation notes." : string.Join(" ", issues);
        IsValid = issues.Count == 0;
    }

    private static IReadOnlyList<string> ParseLines(string value)
        => value
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

    private static string? ValidateDefinition(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "Blueprint definition is empty.";

        var trimmed = value.Trim();
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

        var lines = trimmed.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
        var hasYamlTokens = lines.Any(line => line.Contains(":", StringComparison.Ordinal) || line.TrimStart().StartsWith("-", StringComparison.Ordinal));
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
