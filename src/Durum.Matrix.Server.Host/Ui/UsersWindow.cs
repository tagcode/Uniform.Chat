namespace Durum.Matrix.Server.Host.Ui;

using Uniform.Matrix.Gui.Terminal;
using Uniform.Matrix.Server.Accounts;
using global::Terminal.Gui.App;

/// <summary>Accounts of the server: create users, reset passwords, give admin rights, deactivate.</summary>
public sealed class UsersWindow : AdminWindow<AdminUser>
{
    private readonly AdminService admin;

    /// <summary>Build the window and load the users.</summary>
    public UsersWindow(IApplication app, AdminService admin)
        : base(app, "Users", $"{"User",-36} {"Name",-24} {"Devices",7} {"Rooms",6}  Status")
    {
        // Actions, then the list
        this.admin = admin;
        AddAction("_New…", Create);
        AddAction("_Password…", ResetPassword);
        AddAction("_Admin", ToggleAdmin);
        AddAction("_Deactivate", ToggleDeactivated);
        Reload();
    }

    /// <summary>Load the accounts.</summary>
    protected override Task<IReadOnlyList<AdminUser>> LoadAsync() => admin.GetUsersAsync(CancellationToken.None);

    /// <summary>One account per row.</summary>
    protected override string FormatRow(AdminUser user) => $"{Clip(user.UserId, 36),-36} {Clip(user.DisplayName, 24),-24} {user.Devices,7} {user.Rooms,6}  {Status(user)}";

    /// <summary>Details of the account.</summary>
    protected override string Describe(AdminUser user) =>
        $"{user.UserId}{(user.DisplayName is null ? "" : $" ({user.DisplayName})")}\nCreated {user.Created.ToLocalTime():yyyy-MM-dd HH:mm}; {user.Devices} logged-in devices; joined to {user.Rooms} rooms. {Status(user)}";

    /// <summary>Ask for a user name, password and admin rights, and create the account.</summary>
    private void Create()
    {
        // Name, password twice, admin
        if (Dialogs.Ask(Tui, "New user", "User name (the part before the colon):") is not string name) return;
        if (AskPassword("New user", $"Password for {name}:") is not string password) return;
        bool isAdmin = Dialogs.Choose(Tui, "New user", $"Make {name} a server admin?", ["_No", "_Yes"]) == 1;

        // Create and show
        string userId = "";
        if (!Run("New user", async () => userId = await admin.CreateUserAsync(name, password, isAdmin, CancellationToken.None))) return;
        Dialogs.Info(Tui, "New user", $"Created {userId}.\n\nThey log in with this user ID and the password.");
        Reload();
    }

    /// <summary>Set a new password for the selected user and log out their devices.</summary>
    private void ResetPassword()
    {
        // New password twice
        if (Selected is not AdminUser user) return;
        if (AskPassword("Reset password", $"New password for {user.UserId}.\nEvery device of the user is logged out.") is not string password) return;
        if (Run("Reset password", () => admin.ResetPasswordAsync(user.UserId, password, CancellationToken.None))) Dialogs.Info(Tui, "Reset password", $"Set a new password for {user.UserId} and logged out {user.Devices} devices.");
        Reload();
    }

    /// <summary>Give or take the selected user's admin rights.</summary>
    private void ToggleAdmin()
    {
        // Confirm the change
        if (Selected is not AdminUser user) return;
        string question = user.Admin ? $"Take server admin rights from {user.UserId}?" : $"Make {user.UserId} a server admin?";
        if (!Dialogs.Confirm(Tui, "Admin", question)) return;
        Run("Admin", () => admin.SetAdminAsync(user.UserId, !user.Admin, CancellationToken.None));
        Reload();
    }

    /// <summary>Deactivate or reactivates the selected user.</summary>
    private void ToggleDeactivated()
    {
        // Confirm the change
        if (Selected is not AdminUser user) return;
        string question = user.Deactivated ? $"Reactivate {user.UserId}? They can log in again." : $"Deactivate {user.UserId}? Every device is logged out, and they cannot log in until reactivated.";
        if (!Dialogs.Confirm(Tui, "Deactivate", question)) return;
        Run("Deactivate", () => admin.SetDeactivatedAsync(user.UserId, !user.Deactivated, CancellationToken.None));
        Reload();
    }

    /// <summary>Ask for a password twice; return it when both match, null when cancelled.</summary>
    private string? AskPassword(string title, string label)
    {
        // Until the two match, or cancelled
        while (true)
        {
            if (Dialogs.Ask(Tui, title, label, secret: true) is not string password) return null;
            if (Dialogs.Ask(Tui, title, "Type the password again:", secret: true) is not string again) return null;
            if (password == again) return password;
            Dialogs.Error(Tui, title, "The passwords differ. Try again.");
        }
    }

    /// <summary>Status words of an account.</summary>
    private static string Status(AdminUser user) => user.Deactivated ? "deactivated" : user.Admin ? "admin" : "";
}
