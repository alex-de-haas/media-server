"use client";

import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { mediaServer, type BlurayInspection, type BlurayPlaylist, type BlurayTrackSelection, type LibraryMediaSource } from "@/lib/media-server";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { toast } from "@/lib/toast";
import { describePlaylists, playlistDuration } from "@/lib/bluray-playlists";

export function BlurayDialog({ source, runtimeTicks, onClose }: { source: LibraryMediaSource; runtimeTicks?: number | null; onClose: () => void }) {
  const [playlistId, setPlaylistId] = useState("");
  const inspection = useQuery({
    queryKey: ["bluray-inspect", source.id, playlistId],
    queryFn: () => mediaServer.inspectBluray(source.id, playlistId || undefined),
    staleTime: 0, retry: false,
  });
  const playlist = inspection.data?.playlists.find(p => p.id === playlistId);
  const expectedSeconds = runtimeTicks && runtimeTicks > 0 ? runtimeTicks / 1e7 : undefined;
  const { sorted, candidateIds, ambiguous, matchedRuntime } = describePlaylists(inspection.data?.playlists ?? [], expectedSeconds);
  return <Dialog open onOpenChange={open => { if (!open) onClose(); }}>
    <DialogContent className="max-h-[85vh] overflow-y-auto sm:max-w-2xl">
      <DialogHeader><DialogTitle>Create MKV</DialogTitle>
        <DialogDescription>Choose the film playlist and tracks. The original disc stays in your library. Video and selected tracks are copied without re-encoding.</DialogDescription>
      </DialogHeader>
      {inspection.isPending && <p role="status">Inspecting Blu-ray…</p>}
      {inspection.error && <div role="alert"><p>{inspection.error.message}</p><Button variant="outline" onClick={() => inspection.refetch()}>Retry inspection</Button></div>}
      {inspection.data && <>
        <p id="playlist-help" className="text-muted-foreground text-sm">
          A playlist tells the disc which video segments to play and in what order. It may contain the film, an alternate cut, or extras. The number is a disc reference, not a title.
        </p>
        <label className="flex flex-col gap-2 text-sm">Playlist
          <select aria-label="Playlist" aria-describedby="playlist-help playlist-hint" className="w-full min-w-0 rounded-md border bg-background p-2" value={playlistId} onChange={e => setPlaylistId(e.target.value)}>
            <option value="">Select a playlist</option>
            {sorted.map(p => <option key={p.id} value={p.id} disabled={Boolean(p.error)}>
              {playlistDuration(p.durationSeconds)} — {candidateIds.has(p.id) ? "Possible main film" : "Playlist"} · {p.id}{p.error ? " — Unavailable" : ""}
            </option>)}
          </select>
        </label>
        <p id="playlist-hint" className="text-muted-foreground text-sm">
          {expectedSeconds && <>Movie runtime from library metadata: {playlistDuration(expectedSeconds)}. </>}
          {matchedRuntime && <>Playlists close to this runtime appear first. </>}
          {ambiguous
            ? "Several playlists have similar running times. They may be alternate cuts or repeated versions. Duration alone cannot identify the film; compare the tracks and chapters before choosing."
            : matchedRuntime
              ? "“Possible main film” is a runtime match, not a confirmed title. Check its tracks before choosing."
              : "Longest playlists appear first. “Possible main film” is a duration-based hint, not a confirmed title. Shorter playlists may be extras."}
        </p>
        {!inspection.data.playlists.length && <p role="alert">No usable playlists found.</p>}
        {playlist?.error && <p role="alert">{playlist.error}</p>}
        {playlist && !playlist.error && <div className="rounded-md border p-3 text-sm">
          <p className="font-medium">{playlistDuration(playlist.durationSeconds)} · {playlist.chapters} chapters · {playlist.clips.length} video {playlist.clips.length === 1 ? "segment" : "segments"}</p>
          <p className="mt-1 text-muted-foreground">These segments are combined into one MKV in playlist order.</p>
          <details className="mt-2">
            <summary className="cursor-pointer">Disc file details</summary>
            <p className="mt-2 font-mono">Playlist: {playlist.id}.mpls</p>
            <ol className="mt-1 max-h-32 list-inside list-decimal overflow-y-auto font-mono">
              {playlist.clips.map((clip, index) => <li key={`${index}:${clip}`} className="break-all">{clip}</li>)}
            </ol>
          </details>
        </div>}
        {playlist && !playlist.error && <TrackSelection key={`${playlist.id}:${inspection.data.revision}`} sourceId={source.id} playlist={playlist} inspection={inspection.data} onClose={onClose} />}
      </>}
    </DialogContent>
  </Dialog>;
}

function TrackSelection({ sourceId, playlist, inspection, onClose }: {
  sourceId: string; playlist: BlurayPlaylist; inspection: BlurayInspection; onClose: () => void;
}) {
  const candidates = playlist.tracks.filter(t => t.type === "audio" || t.type === "subtitles");
  const [selected, setSelected] = useState<number[]>(() => {
    const audio = candidates.find(t => t.type === "audio" && t.default) ?? candidates.find(t => t.type === "audio");
    return audio ? [audio.id] : [];
  });
  const [settings, setSettings] = useState<Record<number, BlurayTrackSelection>>(() => Object.fromEntries(candidates.map(t => [t.id, {
    id: t.id, language: t.language, title: t.title, default: t.default, forced: t.forced,
  }])));
  const [name, setName] = useState("Blu-ray MKV");
  const queryClient = useQueryClient();
  const video = playlist.tracks.find(t => t.type === "video");
  const tracks = (type: string) => candidates.filter(t => t.type === type && selected.includes(t.id)).map(t => settings[t.id]);
  const audio = tracks("audio");
  const subtitles = tracks("subtitles");
  const mutation = useMutation({
    mutationFn: () => mediaServer.createBlurayMkv(sourceId, {
      revision: inspection.revision, playlistId: playlist.id, videoTrackId: video!.id, audio, subtitles,
    }, name),
    onSuccess: () => { queryClient.invalidateQueries({ queryKey: ["transcode-jobs"] }); toast.success("MKV creation queued"); onClose(); },
  });
  function update(id: number, change: Partial<BlurayTrackSelection>) {
    setSettings(current => {
      const next = { ...current, [id]: { ...current[id], ...change } };
      if (change.default) {
        const type = candidates.find(t => t.id === id)?.type;
        candidates.filter(t => t.id !== id && t.type === type).forEach(t => { next[t.id] = { ...next[t.id], default: false }; });
      }
      return next;
    });
  }
  return <form className="flex flex-col gap-4" onSubmit={e => { e.preventDefault(); mutation.mutate(); }}>
    <p className="text-sm">Primary video: {video ? `${video.codec} ${video.pixelDimensions ?? ""}` : "Unavailable"}. Chapters are preserved.</p>
    {["audio", "subtitles"].map(type => <fieldset key={type} className="flex flex-col gap-3" disabled={mutation.isPending}>
      <legend className="mb-2 font-medium">{type === "audio" ? "Audio — select at least one" : "Subtitles — optional"}</legend>
      {candidates.filter(t => t.type === type).map(track => {
        const checked = selected.includes(track.id);
        const setting = settings[track.id];
        return <div key={track.id} className="rounded-md border p-3 text-sm">
          <label className="flex items-center gap-2"><input type="checkbox" checked={checked} onChange={e => setSelected(current => e.target.checked ? [...current, track.id] : current.filter(id => id !== track.id))} />
            Track {track.id}: {track.codec} · {track.language ?? "Unknown language"}{track.channels ? ` · ${track.channels} channels` : ""}{track.title ? ` · ${track.title}` : ""}
          </label>
          {checked && <div className="mt-3 grid grid-cols-2 gap-3">
            <label>Language<Input value={setting.language ?? ""} maxLength={35} onChange={e => update(track.id, { language: e.target.value || null })} /></label>
            <label>Title<Input value={setting.title ?? ""} maxLength={256} onChange={e => update(track.id, { title: e.target.value })} /></label>
            <label className="flex items-center gap-2"><input type="checkbox" checked={setting.default} onChange={e => update(track.id, { default: e.target.checked })} />Default</label>
            <label className="flex items-center gap-2"><input type="checkbox" checked={setting.forced} onChange={e => update(track.id, { forced: e.target.checked })} />Forced</label>
          </div>}
        </div>;
      })}
    </fieldset>)}
    <label className="text-sm">Version name<Input value={name} maxLength={120} required onChange={e => setName(e.target.value)} /></label>
    <p className="text-muted-foreground text-sm">The MKV is stored outside the disc directory as a new library version. Reserve up to {(inspection.sizeBytes / 1024 ** 3).toFixed(1)} GiB of additional space; the engine checks free space before writing.</p>
    {mutation.error && <p role="alert" className="text-destructive text-sm">{mutation.error.message}</p>}
    <div className="flex justify-end gap-2"><Button type="button" variant="outline" onClick={onClose}>Close</Button>
      <Button type="submit" disabled={!video || !audio.length || mutation.isPending}>{mutation.isPending ? "Queuing…" : "Create MKV"}</Button></div>
  </form>;
}
