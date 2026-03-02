#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Shoots.Contracts.Core;

namespace Shoots.Ui.ViewModels
{
    // Minimal INotifyPropertyChanged base to avoid depending on CommunityToolkit types.
    public abstract class NotifyBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;

            field = value;
            OnPropertyChanged(name);
            return true;
        }
    }

    public sealed class ProviderCapabilityMatrixRow : NotifyBase
    {
        private string _providerId = "";
        public string ProviderId { get => _providerId; set => SetProperty(ref _providerId, value); }

        private string _capability = "";
        public string Capability { get => _capability; set => SetProperty(ref _capability, value); }

        private bool _supported;
        public bool Supported { get => _supported; set => SetProperty(ref _supported, value); }

        // Your MainWindowViewModel calls this. The stub must provide it.
        public static ProviderCapabilityMatrixRow FromKind(ProviderKind kind)
            => new ProviderCapabilityMatrixRow
            {
                ProviderId = kind.ToString(),
                Capability = "ui-compile-stub",
                Supported = true
            };
    }
}

namespace Shoots.UI.ExecutionEnvironments
{
    // Minimal placeholder to satisfy UI compilation. Do NOT construct RootFsDescriptor here,
    // because the real RootFsDescriptor requires ctor args and will explode at compile-time.
    public sealed class ExecutionEnvironmentSettings
    {
        public string Id { get; }
        public IReadOnlyList<RootFsDescriptor> Roots { get; }
        public string Notes { get; }

        public ExecutionEnvironmentSettings()
            : this("none", Array.Empty<RootFsDescriptor>(), string.Empty) { }

        public ExecutionEnvironmentSettings(string id)
            : this(id, Array.Empty<RootFsDescriptor>(), string.Empty) { }

        public ExecutionEnvironmentSettings(IReadOnlyList<RootFsDescriptor> roots)
            : this("none", roots ?? Array.Empty<RootFsDescriptor>(), string.Empty) { }

        public ExecutionEnvironmentSettings(string id, IReadOnlyList<RootFsDescriptor> roots)
            : this(id, roots ?? Array.Empty<RootFsDescriptor>(), string.Empty) { }

        public ExecutionEnvironmentSettings(string id, IReadOnlyList<RootFsDescriptor> roots, string notes)
        {
            Id = string.IsNullOrWhiteSpace(id) ? "none" : id;
            Roots = roots ?? Array.Empty<RootFsDescriptor>();
            Notes = notes ?? string.Empty;
        }
    }
}