using System.Text.Json;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Folders;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Mailcoded.Daemon;
using Mailcoded.Protocol;
using Xunit;

namespace Mailcoded.Core.Tests.Daemon;

public sealed class FolderHierarchyWireTests
{
    [Fact]
    public void The_list_is_a_tree_in_display_order_with_depth_leaf_and_nearest_listed_parent()
    {
        var folders = new[]
        {
            Summary(5, "Work/Q1/Plans"),
            Summary(2, "Work"),
            Summary(1, "INBOX", FolderRole.Inbox),
            Summary(3, "Work/Q1"),
            Summary(4, "Sent", FolderRole.Sent),
        };

        var dtos = WireMapper.ToDtos(folders);

        Assert.Equal(["INBOX", "Sent", "Work", "Work/Q1", "Work/Q1/Plans"], dtos.Select(d => d.Name));
        Assert.Equal(["INBOX", "Sent", "Work", "Q1", "Plans"], dtos.Select(d => d.Leaf));
        Assert.Equal([0, 0, 0, 1, 2], dtos.Select(d => d.Depth));
        Assert.Equal([null, null, null, 2L, 3L], dtos.Select(d => d.ParentId));
    }

    [Fact]
    public void An_implied_parent_is_not_a_folder_on_the_wire_but_its_depth_shows_in_the_child()
    {
        var dtos = WireMapper.ToDtos([Summary(7, "Clients/Acme/2026")]);

        var only = Assert.Single(dtos);
        Assert.Equal(7, only.Id);
        Assert.Equal("2026", only.Leaf);
        Assert.Equal(2, only.Depth);
        Assert.Null(only.ParentId);
    }

    [Fact]
    public void The_delimiter_is_the_servers_own_and_absent_for_a_flat_namespace()
    {
        var dtos = WireMapper.ToDtos(
        [
            new FolderSummary { Id = new FolderId(1), AccountId = new AccountId(1), Path = FolderPath.Create("INBOX.Work", '.') },
            new FolderSummary { Id = new FolderId(2), AccountId = new AccountId(1), Path = FolderPath.Create("a/b", '\0') },
        ]);

        var dotted = dtos.Single(d => d.Id == 1);
        Assert.Equal("INBOX/Work", dotted.Name);
        Assert.Equal(".", dotted.Delimiter);

        var flat = dtos.Single(d => d.Id == 2);
        Assert.Equal("a/b", flat.Name);
        Assert.Null(flat.Delimiter);
        Assert.Equal(0, flat.Depth);
        Assert.Equal("a/b", flat.Leaf);
    }

    [Fact]
    public void A_single_folder_carries_the_same_placement_as_in_the_whole_list()
    {
        var all = new[] { Summary(1, "Work"), Summary(2, "Work/Q1") };

        var single = WireMapper.ToDto(all[1], all);
        var listed = WireMapper.ToDtos(all).Single(d => d.Id == 2);

        Assert.Equal(listed, single);
        Assert.Equal(1L, single.ParentId);
    }

    [Fact]
    public void The_hierarchy_fields_serialise_camel_case_and_optional_ones_are_left_out_when_null()
    {
        var dto = WireMapper.ToDtos([Summary(1, "INBOX", FolderRole.Inbox)]).Single();

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(dto, ProtocolJsonContext.Default.FolderDto));
        var names = document.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(["accountId", "delimiter", "depth", "id", "leaf", "name", "role", "total", "unread"], names);
        Assert.Equal("/", document.RootElement.GetProperty("delimiter").GetString());
    }

    [Fact]
    public void A_reply_from_a_daemon_without_the_new_fields_still_deserialises()
    {
        const string old = "{\"id\":1,\"accountId\":1,\"name\":\"INBOX\",\"role\":\"inbox\",\"unread\":2,\"total\":3}";

        var dto = JsonSerializer.Deserialize(old, ProtocolJsonContext.Default.FolderDto);

        Assert.NotNull(dto);
        Assert.Null(dto.Leaf);
        Assert.Null(dto.Delimiter);
        Assert.Null(dto.ParentId);
        Assert.Equal(0, dto.Depth);
    }

    [Fact]
    public void A_hostile_name_reaches_the_wire_as_data_and_never_as_a_raw_control_byte()
    {
        var dto = WireMapper.ToDtos([Summary(1, "Work/\u001b]0;pwned\u0007\u202Eevil")]).Single();

        var json = JsonSerializer.Serialize(dto, ProtocolJsonContext.Default.FolderDto);

        Assert.DoesNotContain('\u001b', json);
        Assert.DoesNotContain('\u0007', json);
        Assert.Equal("\u001b]0;pwned\u0007\u202Eevil", dto.Leaf);
    }

    private static FolderSummary Summary(long id, string name, FolderRole role = FolderRole.None) =>
        new() { Id = new FolderId(id), AccountId = new AccountId(1), Path = FolderPath.Create(name), Role = role };
}
