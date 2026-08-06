using System;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace GlassCoder.Wpf.Services;

/// <summary>
/// Small pieces of desktop state that survive a restart - the last goal, so a repeated test run
/// is a press of Run rather than a paste.
/// <para>
/// Deliberately <em>not</em> the user settings store. Everything saved there feeds
/// <c>IConfiguration</c>, and the provenance stamp hashes the effective configuration so that a
/// run's arm is identifiable (<c>ProvenanceStamp.ConfigHash</c>) - state that changes with every
/// run would relabel every arm and make no two runs comparable. UI state therefore lives where
/// configuration never looks.
/// </para>
/// </summary>
public interface IUiStateStore
{
    /// <summary>The goal the last run was started with, or null when none has been saved.</summary>
    string? LastGoal { get; set; }
}

/// <summary>
/// The Windows implementation, under <c>HKCU\Software\GlassCoder</c>.
/// <para>
/// Every failure path returns the absence of a convenience rather than an error: a machine
/// where the key cannot be read starts with an empty goal box, exactly like a first-ever start,
/// and a save that fails loses nothing but the pre-fill.
/// </para>
/// </summary>
public sealed class RegistryUiStateStore : IUiStateStore
{
    private const string KeyPath = @"Software\GlassCoder";
    private const string LastGoalName = "LastGoal";

    /// <inheritdoc />
    public string? LastGoal
    {
        get
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);
                return key?.GetValue(LastGoalName) as string;
            }
            catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        set
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath);
                if (string.IsNullOrWhiteSpace(value))
                {
                    key.DeleteValue(LastGoalName, throwOnMissingValue: false);
                }
                else
                {
                    key.SetValue(LastGoalName, value);
                }
            }
            catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
            {
                // Losing the pre-fill is not worth interrupting a run over.
            }
        }
    }
}
