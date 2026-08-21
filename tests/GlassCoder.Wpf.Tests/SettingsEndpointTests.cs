using System.Windows;
using System.Windows.Controls;
using GlassCoder.Core.Configuration;
using GlassCoder.Models;
using GlassCoder.Models.Configuration;
using GlassCoder.TestSupport;
using GlassCoder.Wpf.ViewModels;
using GlassCoder.Wpf.Views;
using Microsoft.Extensions.Configuration;

namespace GlassCoder.Wpf.Tests;

/// <summary>
/// The endpoint picker in the settings dialog: the list every role chooses its endpoint from,
/// and the two buttons that curate it.
/// <para>
/// The property worth asserting is the separation. The list is what the dialog <em>offers</em>;
/// a role is served by its own <see cref="ModelRoleOptions.Endpoint"/> and by nothing else. If
/// forgetting an address could re-point a role, an operator tidying the list would silently move
/// a role onto a server that answers nothing, and would find out on the next run.
/// </para>
/// </summary>
public sealed class SettingsEndpointTests
{
    [Fact]
    public void The_picker_starts_from_the_endpoints_the_roles_are_already_on()
    {
        // A configuration written before the list existed has no list, and every one of them has
        // roles. Seeding from those is what stops the picker opening empty on an install that has
        // been working for months.
        using Fixture fixture = new(Configured(
            ("worker", "http://localhost:8002/v1"),
            ("critic", "http://localhost:8003/v1")));

        // Order follows the roles, which the binder hands over sorted rather than as written.
        fixture.ViewModel.Endpoints.ShouldBe(
            ["http://localhost:8002/v1", "http://localhost:8003/v1"], ignoreOrder: true);
    }

    [Fact]
    public void Two_roles_on_one_server_are_offered_once()
    {
        using Fixture fixture = new(Configured(
            ("worker", "http://localhost:8002/v1"),
            ("critic", "http://localhost:8002/v1")));

        fixture.ViewModel.Endpoints.ShouldBe(["http://localhost:8002/v1"]);
    }

    [Fact]
    public void A_saved_list_is_the_list_and_is_not_topped_up_from_the_roles()
    {
        // Curated means curated: an address removed on the last visit must not come back because
        // some role still happens to point at it, or Forget would only ever appear to work.
        using Fixture fixture = new(Configured(("worker", "http://localhost:8002/v1"))
            .Concat([new KeyValuePair<string, string?>(
                "GlassCoder:Models:KnownEndpoints:0", "https://api.anthropic.com")]));

        fixture.ViewModel.Endpoints.ShouldBe(["https://api.anthropic.com"]);
    }

    [Fact]
    public void An_endpoint_the_harness_would_reject_is_never_offered()
    {
        using Fixture fixture = new(Configured(("worker", "http://localhost:8002/v1")));

        fixture.ViewModel.SelectedRole!.Endpoint = "localhost:8002";
        fixture.ViewModel.AddEndpointCommand.Execute(null);

        fixture.ViewModel.Endpoints.ShouldNotContain("localhost:8002");
        fixture.ViewModel.Status.ShouldContain("absolute http(s) URL");
    }

    [Fact]
    public void Remembering_an_endpoint_offers_it_to_every_role()
    {
        using Fixture fixture = new(Configured(
            ("worker", "http://localhost:8002/v1"),
            ("critic", "http://localhost:8003/v1")));

        fixture.ViewModel.SelectedRole!.Endpoint = "https://api.anthropic.com";
        fixture.ViewModel.AddEndpointCommand.Execute(null);

        // One list, not one per role: pointing the critic at the worker's server is the thing
        // this exists to make a pick rather than a retyped URL.
        fixture.ViewModel.Endpoints.ShouldContain("https://api.anthropic.com");
    }

    [Fact]
    public void Forgetting_an_endpoint_leaves_the_role_pointed_at_it()
    {
        using Fixture fixture = new(Configured(("worker", "http://localhost:8002/v1")));

        fixture.ViewModel.RemoveEndpointCommand.Execute(null);

        fixture.ViewModel.Endpoints.ShouldBeEmpty();
        fixture.ViewModel.SelectedRole!.Endpoint.ShouldBe("http://localhost:8002/v1");
        fixture.ViewModel.Settings.Models.Roles["worker"].Endpoint.ShouldBe("http://localhost:8002/v1");
    }

    [Fact]
    public void A_save_records_what_the_picker_was_left_holding()
    {
        using Fixture fixture = new(Configured(("worker", "http://localhost:8002/v1")));

        fixture.ViewModel.SelectedRole!.Endpoint = "https://api.anthropic.com";
        fixture.ViewModel.AddEndpointCommand.Execute(null);
        fixture.ViewModel.SaveCommand.Execute(null);

        GlassCoderSettings saved = GlassCoderSettings.ReadFrom(
            new ConfigurationBuilder().AddGlassCoderUserSettings(fixture.Store).Build());

        saved.Models.KnownEndpoints.ShouldBe(["http://localhost:8002/v1", "https://api.anthropic.com"]);
    }

    [Fact]
    public void Retyping_an_endpoint_updates_the_role_list_beside_the_editor()
    {
        // The list shows each role's endpoint under its name. It binds to the view model rather
        // than to the options object, which raises nothing - without that the list went on
        // showing the endpoint the dialog opened on.
        using Fixture fixture = new(Configured(("worker", "http://localhost:8002/v1")));

        List<string> raised = [];
        RoleSettingsViewModel role = fixture.ViewModel.SelectedRole!;
        role.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

        role.Endpoint = "http://localhost:8009/v1";

        raised.ShouldContain(nameof(RoleSettingsViewModel.Endpoint));
        role.Options.Endpoint.ShouldBe("http://localhost:8009/v1");
    }

    [Fact]
    public void The_dialog_hands_the_picker_the_list_and_the_role_its_endpoint()
    {
        // The picker sits in the pane bound to the selected role, so it reaches the list and the
        // two buttons through the window rather than through its own data context. That lookup
        // fails silently when it is wrong - an empty dropdown and two dead buttons, with nothing
        // in the build to say so - which is the whole reason this test builds the window.
        using Fixture fixture = new(Configured(
            ("worker", "http://localhost:8002/v1"),
            ("critic", "http://localhost:8003/v1")));

        (int Offered, string Text, bool CanForget) picker = UiThread.Run(_ =>
        {
            TestApplication.Ensure();
            SettingsWindow window = new(fixture.ViewModel);

            // Shown, because the picker lives inside a tab. A tab's content is not in the visual
            // tree until the tab is realised, and a binding that walks up to the window has no
            // window to find until it is - so an unshown dialog reports an empty dropdown whether
            // the binding is right or wrong, which is not a test.
            window.ShowInTaskbar = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -32000;
            window.Top = -32000;
            window.Show();
            window.UpdateLayout();

            try
            {
                ComboBox box = (ComboBox)window.FindName("EndpointPicker");
                return (box.Items.Count, box.Text, fixture.ViewModel.RemoveEndpointCommand.CanExecute(null));
            }
            finally
            {
                window.Close();
            }
        });

        picker.Offered.ShouldBe(2);
        picker.Text.ShouldBe(fixture.ViewModel.SelectedRole!.Endpoint);
        picker.CanForget.ShouldBeTrue();
    }

    private static IEnumerable<KeyValuePair<string, string?>> Configured(
        params (string Role, string Endpoint)[] roles)
    {
        foreach ((string role, string endpoint) in roles)
        {
            yield return new KeyValuePair<string, string?>(
                $"GlassCoder:Models:Roles:{role}:Endpoint", endpoint);
            yield return new KeyValuePair<string, string?>(
                $"GlassCoder:Models:Roles:{role}:ModelAlias", role);
        }

        yield return new KeyValuePair<string, string?>("GlassCoder:Models:DefaultRole", roles[0].Role);
        yield return new KeyValuePair<string, string?>("GlassCoder:Agent:Role", roles[0].Role);
    }

    /// <summary>
    /// The dialog over a throwaway settings directory. The stores are the real ones - what a save
    /// writes and what a load reads back is half of what these tests are about - and only the
    /// probe is faked, because nothing here presses Test.
    /// </summary>
    private sealed class Fixture : IDisposable
    {
        private readonly TempWorkspace _workspace = new();

        public Fixture(IEnumerable<KeyValuePair<string, string?>> configuration)
        {
            Store = new UserSettingsStore(new DpapiSecretProtector(), _workspace.Root);

            ViewModel = new SettingsViewModel(
                new ConfigurationBuilder().AddInMemoryCollection(configuration).Build(),
                Store,
                new ProjectSettingsStore(),
                new SettingsTransfer(),
                new SilentProbe(),
                new FakeShell());
        }

        public UserSettingsStore Store { get; }

        public SettingsViewModel ViewModel { get; }

        public void Dispose() => _workspace.Dispose();
    }

    private sealed class SilentProbe : IModelConnectionProbe
    {
        public Task<ConnectionCheckResult> CheckAsync(
            string role,
            ModelRoleOptions settings,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("No test here presses Test.");
    }
}
