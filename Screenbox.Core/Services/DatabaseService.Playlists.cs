using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Screenbox.Core.Enums;
using Screenbox.Core.Models;

namespace Screenbox.Core.Services;

public sealed partial class DatabaseService
{
    /// <inheritdoc/>
    public async Task SavePlaylistAsync(PlaylistRecordDto playlist)
    {
        await EnsureInitializedAsync();
        using var connection = CreateConnection();
        using var transaction = connection.BeginTransaction();

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO playlists (id, display_name, last_updated, custom_thumbnail)
                VALUES (@id, @name, @updated, @customThumb)
                ON CONFLICT(id) DO UPDATE SET
                    display_name = excluded.display_name,
                    last_updated = excluded.last_updated,
                    custom_thumbnail = excluded.custom_thumbnail;
                """;
            cmd.Parameters.AddWithValue("@id", playlist.Id);
            cmd.Parameters.AddWithValue("@name", playlist.DisplayName);
            cmd.Parameters.AddWithValue("@updated", playlist.LastUpdated.UtcTicks);
            cmd.Parameters.AddWithValue("@customThumb", (object?)playlist.CustomThumbnailPath ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM playlist_items WHERE playlist_id = @id;";
            cmd.Parameters.AddWithValue("@id", playlist.Id);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO playlist_items
                    (playlist_id, path, sort_order)
                VALUES
                    (@pid, @path, @order);
                """;

            var p = cmd.Parameters;
            p.AddWithValue("@pid", playlist.Id);
            var pPath = p.Add("@path", SqliteType.Text);
            var pOrder = p.Add("@order", SqliteType.Integer);

            for (int i = 0; i < playlist.Items.Count; i++)
            {
                RawMediaRecordDto item = playlist.Items[i];
                pPath.Value = item.Path;
                pOrder.Value = i;
                cmd.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    /// <inheritdoc/>
    public async Task<PlaylistRecordDto?> LoadPlaylistAsync(string id)
    {
        await EnsureInitializedAsync();
        using var connection = CreateConnection();
        PlaylistRecordDto? playlist = null;

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id, display_name, last_updated, custom_thumbnail FROM playlists WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                playlist = new PlaylistRecordDto
                {
                    Id = reader.GetString(0),
                    DisplayName = reader.GetString(1),
                    LastUpdated = new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero),
                    CustomThumbnailPath = reader.IsDBNull(3) ? null : reader.GetString(3),
                };
            }
        }

        if (playlist is null)
        {
            return null;
        }

        playlist.Items = ReadPlaylistItems(connection, id);
        return playlist;
    }

    /// <inheritdoc/>
    public async Task<List<PlaylistRecordDto>> ListPlaylistsAsync()
    {
        await EnsureInitializedAsync();
        using var connection = CreateConnection();

        var playlists = new List<PlaylistRecordDto>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id, display_name, last_updated, custom_thumbnail FROM playlists ORDER BY last_updated DESC;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                playlists.Add(new PlaylistRecordDto
                {
                    Id = reader.GetString(0),
                    DisplayName = reader.GetString(1),
                    LastUpdated = new DateTimeOffset(reader.GetInt64(2), TimeSpan.Zero),
                    CustomThumbnailPath = reader.IsDBNull(3) ? null : reader.GetString(3),
                });
            }
        }

        foreach (PlaylistRecordDto playlist in playlists)
        {
            playlist.Items = ReadPlaylistItems(connection, playlist.Id);
        }

        return playlists;
    }

    /// <inheritdoc/>
    public async Task DeletePlaylistAsync(string id)
    {
        await EnsureInitializedAsync();
        using var connection = CreateConnection();

        string? customThumbPath = null;
        using (var selectCmd = connection.CreateCommand())
        {
            selectCmd.CommandText = "SELECT custom_thumbnail FROM playlists WHERE id = @id;";
            selectCmd.Parameters.AddWithValue("@id", id);
            if (selectCmd.ExecuteScalar() is string path && !string.IsNullOrWhiteSpace(path))
            {
                customThumbPath = path;
            }
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM playlists WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();
        }

        if (!string.IsNullOrEmpty(customThumbPath))
        {
            try
            {
                string fullLocalRoot = Path.GetFullPath(DbFolderPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string fullThumbPath = Path.GetFullPath(customThumbPath);

                if (fullThumbPath.StartsWith(fullLocalRoot, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(fullThumbPath))
                {
                    File.Delete(fullThumbPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete custom playlist thumbnail '{Path}'.", customThumbPath);
            }
        }
    }

    private static List<RawMediaRecordDto> ReadPlaylistItems(SqliteConnection connection, string playlistId)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = SelectPlaylistItemsWithMetadataSql;
        cmd.Parameters.AddWithValue("@pid", playlistId);

        var items = new List<RawMediaRecordDto>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string path = reader.GetString(0);
            string fallbackTitle = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(fallbackTitle))
            {
                fallbackTitle = path;
            }

            items.Add(new RawMediaRecordDto
            {
                Path = path,
                Title = reader.IsDBNull(1) ? fallbackTitle : reader.GetString(1),
                MediaType = (MediaPlaybackType)(reader.IsDBNull(2) ? (int)MediaPlaybackType.Unknown : reader.GetInt32(2)),
                DateAdded = reader.IsDBNull(3) ? default : new DateTimeOffset(reader.GetInt64(3), TimeSpan.Zero),
                Duration = reader.IsDBNull(4) ? default : TimeSpan.FromTicks(reader.GetInt64(4)),
                Year = reader.IsDBNull(5) ? 0u : (uint)reader.GetInt64(5),
                Artist = reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                Album = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                AlbumArtist = reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                Composers = reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                Genre = reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
                TrackNumber = reader.IsDBNull(11) ? 0u : (uint)reader.GetInt64(11),
                Subtitle = reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
                Producers = reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
                Writers = reader.IsDBNull(14) ? string.Empty : reader.GetString(14),
                Bitrate = reader.IsDBNull(15) ? 0u : (uint)reader.GetInt64(15),
                Width = reader.IsDBNull(16) ? 0u : (uint)reader.GetInt64(16),
                Height = reader.IsDBNull(17) ? 0u : (uint)reader.GetInt64(17),
                VideoBitrate = reader.IsDBNull(18) ? 0u : (uint)reader.GetInt64(18),
            });
        }

        return items;
    }

    private const string SelectPlaylistItemsWithMetadataSql = """
        SELECT pi.path,
               mr.title, mr.media_type, mr.date_added, mr.duration_ticks, mr.year,
               mr.artist, mr.album, mr.album_artist, mr.composers, mr.genre, mr.track_number,
               mr.subtitle, mr.producers, mr.writers, mr.bitrate, mr.width, mr.height, mr.video_bitrate
        FROM playlist_items pi
        LEFT JOIN media_records mr ON mr.path = pi.path
        WHERE pi.playlist_id = @pid
        ORDER BY pi.sort_order, pi.id;
        """;
}
