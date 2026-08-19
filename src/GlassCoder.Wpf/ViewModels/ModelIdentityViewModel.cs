using System;
using System.Globalization;
using System.Linq;
using GlassCoder.Models;
using GlassCoder.Models.Configuration;
using GlassCoder.Wpf.Mvvm;

namespace GlassCoder.Wpf.ViewModels;

/// <summary>
/// One role in the shell's header band: which alias the run will address, and what is actually
/// behind it.
/// <para>
/// This is the application's own thesis applied to itself. <c>capability ≈ model × harness ×
/// context</c> is the frame every metric on the Metrics surface is read through, and until now the
/// window could not say which of those three it was running - every run, on every checkpoint,
/// reported the same alias. The band answers that on sight rather than on hover, because the one
/// fact that changes how you read every other number should not need to be discovered.
/// </para>
/// <para>
/// Display only. Nothing here is saved, and nothing in the harness branches on it: the alias
/// stays the only thing addressed, and serving topology stays below the seam (CLAUDE.md §19).
/// </para>
/// </summary>
public sealed class ModelIdentityViewModel : ViewModelBase
{
    private string _description = "checking…";
    private ConnectionCheckOutcome? _outcome;

    /// <summary>Creates the row for a role, in the state it holds until an answer arrives.</summary>
    /// <param name="role">The role name, as the harness addresses it.</param>
    /// <param name="alias">The served-model alias this role asks for.</param>
    public ModelIdentityViewModel(string role, string alias)
    {
        Role = role;
        Alias = alias;
    }

    /// <summary>The role name - <c>worker</c>, <c>critic</c>.</summary>
    public string Role { get; }

    /// <summary>The alias the role addresses, which is not always the role's own name.</summary>
    public string Alias { get; }

    /// <summary>What is behind the alias, or why that is not known.</summary>
    public string Description
    {
        get => _description;
        private set => SetProperty(ref _description, value);
    }

    /// <summary>
    /// How well the answer went, in the same three-way vocabulary the settings dialog uses, so
    /// amber means one thing across both windows. Null while the answer is still in flight.
    /// </summary>
    public ConnectionCheckOutcome? Outcome
    {
        get => _outcome;
        private set => SetProperty(ref _outcome, value);
    }

    /// <summary>Records that this role was never asked, because it has no key to ask with.</summary>
    public void Unusable() =>
        Set(ConnectionCheckOutcome.Warning, "no API key configured, so it was not asked");

    /// <summary>Records what a directory lookup found.</summary>
    /// <param name="settings">The role's settings - its endpoint is what a failure has to name.</param>
    /// <param name="list">What the server said.</param>
    public void Describe(ModelRoleOptions settings, ServedModelList list)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(list);

        switch (list.Outcome)
        {
            case ServedModelListOutcome.Unreachable:
                // The ordinary state at startup, not a fault: the app opens before the model
                // server does at least as often as the other way round.
                Set(ConnectionCheckOutcome.Warning, $"not available at {settings.Endpoint}");
                return;

            case ServedModelListOutcome.Unauthorized:
                Set(ConnectionCheckOutcome.Failed, $"the key was rejected by {settings.Endpoint}");
                return;

            case ServedModelListOutcome.Refused:
                // Reachable, but this server has no model list. Distinct from being down, and a
                // different fix - so it gets different words rather than a shared "not available".
                Set(ConnectionCheckOutcome.Warning, $"reachable, but {settings.Endpoint} lists no models");
                return;

            default:
                Set(Served(list));
                return;
        }
    }

    private (ConnectionCheckOutcome Outcome, string Description) Served(ServedModelList list)
    {
        if (list.Find(Alias) is not { } model)
        {
            string others = string.Join(", ", list.Models.Select(entry => entry.Alias));

            return (
                ConnectionCheckOutcome.Warning,
                others.Length > 0
                    ? $"'{Alias}' is not served; this endpoint serves {others}"
                    : $"'{Alias}' is not served, and nothing else is either");
        }

        // Two independent facts, either of which the server may withhold: which weights are
        // behind the alias, and how much context they were loaded with.
        string identity = model.Identity ?? "served, checkpoint not reported";

        return model.MaxContextTokens is { } context
            ? (ConnectionCheckOutcome.Ok,
                string.Create(CultureInfo.CurrentCulture, $"{identity} · {context:N0}-token context"))
            : (ConnectionCheckOutcome.Ok, identity);
    }

    private void Set((ConnectionCheckOutcome Outcome, string Description) result) =>
        Set(result.Outcome, result.Description);

    private void Set(ConnectionCheckOutcome outcome, string description)
    {
        Outcome = outcome;
        Description = description;
    }
}
