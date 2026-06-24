using FluentAssertions;
using Gert.Database;
using Gert.Model.Projects;
using Gert.Service.Account;
using Gert.Service.Provisioning;
using Gert.Storage;
using Gert.Testing.Fakes;
using NSubstitute;
using Xunit;

namespace Gert.Service.Tests;

/// <summary>
/// <see cref="UserProvisioner"/> over <c>user.db</c>: seeds the default project,
/// refreshes the username only on change, and finishes an interrupted account
/// deletion (via the journal + eraser) BEFORE provisioning the returning user.
/// </summary>
public sealed class UserProvisionerTests
{
    private readonly IUserDatabaseProvider _databases = Substitute.For<IUserDatabaseProvider>();
    private readonly IUserRepository _repo = Substitute.For<IUserRepository>();
    private readonly TestUserContext _user = new();
    private readonly IDeletionJournal _journal = Substitute.For<IDeletionJournal>();
    private readonly IUserDataEraser _eraser = Substitute.For<IUserDataEraser>();

    public UserProvisionerTests()
    {
        _databases.OpenAsync(_user.Iss, _user.Sub, Arg.Any<CancellationToken>()).Returns(_repo);
        // Steady state: no pending deletion, username already current, default project present.
        _journal.IsPendingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        _repo.GetUsernameAsync(Arg.Any<CancellationToken>()).Returns(_user.Username);
        _repo.GetProjectAsync("default", Arg.Any<CancellationToken>())
            .Returns(new ProjectMeta { Id = "default", Name = "Default", CreatedAt = default, UpdatedAt = default });
    }

    private UserProvisioner NewProvisioner() =>
        new(_databases, _user, TimeProvider.System, _journal, _eraser);

    [Fact]
    public async Task Seeds_the_default_project_when_absent()
    {
        _repo.GetProjectAsync("default", Arg.Any<CancellationToken>()).Returns((ProjectMeta?)null);

        await NewProvisioner().EnsureCurrentUserAsync();

        await _repo.Received(1).SaveProjectAsync(
            Arg.Is<ProjectMeta>(p => p.Id == "default" && p.Name == "Default"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Does_not_reseed_an_existing_default_project()
    {
        await NewProvisioner().EnsureCurrentUserAsync();

        await _repo.DidNotReceive().SaveProjectAsync(Arg.Any<ProjectMeta>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_interrupted_deletion_is_erased_before_provisioning()
    {
        _journal.IsPendingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        await NewProvisioner().EnsureCurrentUserAsync();

        // The owed residue is erased, then the (now-empty) account is opened/provisioned -
        // a returning user never half-resurrects on top of half-deleted data.
        Received.InOrder(() =>
        {
            _eraser.EraseAsync(StorageKeys.UserKey(_user.Iss, _user.Sub), Arg.Any<CancellationToken>());
            _databases.OpenAsync(_user.Iss, _user.Sub, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task No_erase_when_no_deletion_is_owed()
    {
        await NewProvisioner().EnsureCurrentUserAsync();

        await _eraser.DidNotReceive().EraseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Username_is_refreshed_only_when_it_changed()
    {
        _repo.GetUsernameAsync(Arg.Any<CancellationToken>()).Returns("old-name");

        await NewProvisioner().EnsureCurrentUserAsync();

        await _repo.Received(1).SetUsernameAsync(_user.Username, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Username_unchanged_keeps_the_steady_state_path_read_only()
    {
        // GetUsernameAsync already returns _user.Username (the steady state) - no write.
        await NewProvisioner().EnsureCurrentUserAsync();

        await _repo.DidNotReceive().SetUsernameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
