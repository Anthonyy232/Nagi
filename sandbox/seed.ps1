# Writes an invented month of listening into Nagi's database so Insights has something to show.
# The plays are a fixed function of each title, so every run matches.
. $PSScriptRoot\db.ps1

# winsqlite3 has no 'localtime', so local midnight comes in as UTC
$midnight = [DateTime]::Today.ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss')
# Favourites get many plays, everything else a few, spread over the 28 days before today between 9:00 and 23:00.
$sql = @"
DELETE FROM ListenHistory;
WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i < 59),
plays AS (
  SELECT Id, Title, AlbumId,
    CASE Title
      WHEN 'Harbour at Six' THEN 42 WHEN 'Marigold Static' THEN 37 WHEN 'Token Machine' THEN 31
      WHEN 'Signal Garden' THEN 26 WHEN 'Paper Weather' THEN 22 WHEN 'Letters to a Lighthouse' THEN 18
      ELSE (unicode(Title) + length(Title) * 7) % 9
    END AS count
  FROM Songs)
INSERT INTO ListenHistory (SongId, ListenTimestampUtc, IsEligibleForScrobbling, ListenDurationTicks, EndReason, ContextType, ContextId, IsScrobbled, IsSubmittedToListenBrainz, IsSubmittedToServer)
SELECT p.Id,
  strftime('%Y-%m-%d %H:%M:%S', '$midnight', '-' || ((n.i * 7 + length(p.Title)) % 28 + 1) || ' days', '+' || ((n.i * 5 + unicode(p.Title)) % 15 + 9) || ' hours'),
  1, 1800000000, 0, 0, p.AlbumId, 0, 0, 0
FROM plays p JOIN n ON n.i < p.count;
UPDATE Songs SET
  PlayCount = (SELECT count(*) FROM ListenHistory WHERE SongId = Songs.Id),
  LastPlayedDate = (SELECT max(ListenTimestampUtc) FROM ListenHistory WHERE SongId = Songs.Id);
"@

Invoke-NagiSql $sql
