// Purpose: keep UI layer pinned to Contracts + Runtime.Ui.Abstractions only.
// No Shoots.Runtime.Abstractions references are allowed in UI (tests enforce this).

global using System;
global using System.Collections.Generic;
global using System.Collections.ObjectModel;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;

// MVVM (ObservableObject, RelayCommand, AsyncRelayCommand, etc.)
global using CommunityToolkit.Mvvm.ComponentModel;
global using CommunityToolkit.Mvvm.Input;

// UI-facing runtime facade only (read-only, descriptive)
global using Shoots.Runtime.Ui.Abstractions;

// Contracts (catalogs, manifests, ids, etc.)
global using Shoots.Contracts.Core;

// ---- Ambiguity killers ----
// ToolCatalogSnapshot exists in multiple assemblies; UI speaks Contracts.
global using ToolCatalogSnapshot = Shoots.Contracts.Core.ToolCatalogSnapshot;

// RootFsDescriptor: pin to UI's own type.
global using UiRootFsDescriptor = Shoots.UI.ExecutionEnvironments.RootFsDescriptor;