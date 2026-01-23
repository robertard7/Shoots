using System;
using System.ComponentModel;
using System.Linq;
using Shoots.UI.Blueprints;

namespace Shoots.UI.ViewModels;

public sealed class BlueprintEntryViewModel : INotifyPropertyChanged
{
    private readonly Action _onChanged;
    private readonly DateTimeOffset _createdUtc;
    private string _name;
    private string _description;
    private string _intentsText;
    private string _artifactsText;
    private string _validationSummary = string.Empty;
    private bool _isValid = true;

    public BlueprintEntryViewModel(SystemBlueprint blueprint, Action onChanged)
    {
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        _createdUtc = blueprint.CreatedUtc;
        _name = blueprint.Name;
        _description = blueprint.Description;
        _intentsText = string.Join(Environment.NewLine, blueprint.Intents);
        _artifactsText = string.Join(Environment.NewLine, blueprint.Artifacts);
        Validate();
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
            ValidateAndSave();
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
        => new(
            Name.Trim(),
            Description.Trim(),
            ParseLines(IntentsText),
            ParseLines(ArtifactsText),
            _createdUtc);

    private void ValidateAndSave()
    {
        Validate();
        if (IsValid)
            _onChanged();
    }

    private void Validate()
    {
        var issues = new[]
            {
                string.IsNullOrWhiteSpace(Name) ? "Name is empty." : null,
                ParseLines(IntentsText).Count == 0 ? "Add at least one intent line." : null,
                ParseLines(ArtifactsText).Count == 0 ? "Add at least one artifact line." : null
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

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
