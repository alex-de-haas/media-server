"use client";

import { useId, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Folder, Plus, Trash2 } from "lucide-react";
import { toast } from "@/lib/toast";
import { groupsApi, groupTypeLabel, hdrLabel, type GroupInput, type GroupCondition, type GroupSummary } from "@/lib/groups";
import { count } from "@/lib/catalog-scan";
import { errorMessage } from "@/lib/ui";
import { GroupPosters } from "@/components/group-grid";
import { QueryState, ErrorState, Loading } from "@/components/states";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Card, CardAction, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";

const emptyGroup = (): GroupInput => ({ name: "", kind: "manual", catalogType: "movie", rules: { version: 1, match: "all", conditions: [] }, memberIds: [] });
const fields = [{ value: "year", label: "Release year" }, { value: "resolution", label: "Resolution" }, { value: "hdr", label: "HDR format" }, { value: "tag", label: "Tag" }, { value: "genre", label: "Genre" }];

function Choice({ label, value, options, onChange, disabled = false }: { label: string; value: string; options: { value: string; label: string }[]; onChange: (value: string) => void; disabled?: boolean }) {
  return <Select value={value} onValueChange={(v) => { if (v !== null) onChange(v); }} disabled={disabled}>
    <SelectTrigger aria-label={label} className="w-full"><SelectValue>{options.find(o => o.value === value)?.label ?? value}</SelectValue></SelectTrigger>
    <SelectContent>{options.map(o => <SelectItem key={o.value} value={o.value}>{o.label}</SelectItem>)}</SelectContent>
  </Select>;
}

export function GroupsSection() {
  const cache = useQueryClient();
  const groups = useQuery({ queryKey: ["groups"], queryFn: groupsApi.list });
  const [editing, setEditing] = useState<string | null>(null);
  const [deleting, setDeleting] = useState<GroupSummary | null>(null);
  const remove = useMutation({ mutationFn: (id: string) => groupsApi.remove(id), onSuccess: () => {
    void cache.invalidateQueries({ queryKey: ["groups"] }); setDeleting(null); toast.success("Group deleted");
  }, onError: (error) => toast.error("Could not delete group", { description: errorMessage(error) }) });
  return <Card>
    <CardHeader><CardTitle>Groups</CardTitle><CardDescription>Manual selections and smart rules across catalogs of one type.</CardDescription>
      <CardAction><Button onClick={() => setEditing("new")}><Plus /> Add group</Button></CardAction></CardHeader>
    <CardContent><QueryState query={groups} empty="No groups yet. Create your first group.">
      {(items) => <ul className="flex flex-col gap-2">{items.map(g => <li key={g.id} className="flex items-center gap-3 rounded-lg border p-3">
        <Folder className="text-muted-foreground shrink-0" /><div className="min-w-0 flex-1"><p className="truncate font-medium">{g.name}</p>
          <p className="text-muted-foreground text-sm">{groupTypeLabel(g.catalogType)} · {g.kind === "smart" ? "Smart" : "Manual"} · {count(g.itemCount, "title")}</p></div>
        <Button variant="outline" onClick={() => setEditing(g.id)} aria-label={`Edit ${g.name}`}>Edit</Button>
        <Button variant="ghost" onClick={() => setDeleting(g)} aria-label={`Delete ${g.name}`}><Trash2 /></Button>
      </li>)}</ul>}
    </QueryState></CardContent>
    <Dialog open={editing !== null} onOpenChange={open => { if (!open) setEditing(null); }}>
      <DialogContent className="max-h-[90dvh] overflow-y-auto sm:max-w-4xl">
        <DialogHeader><DialogTitle>{editing === "new" ? "Create group" : "Edit group"}</DialogTitle>
          <DialogDescription>Choose one catalog type. Titles keep their files and playback history.</DialogDescription></DialogHeader>
        {editing !== null && <EditorLoader key={editing} id={editing} onClose={() => setEditing(null)} />}
      </DialogContent>
    </Dialog>
    <Dialog open={deleting !== null} onOpenChange={open => { if (!open && !remove.isPending) setDeleting(null); }}>
      <DialogContent><DialogHeader><DialogTitle>Delete {deleting?.name}?</DialogTitle><DialogDescription>The group is removed. Its titles and files remain in the library.</DialogDescription></DialogHeader>
        <DialogFooter><Button variant="outline" disabled={remove.isPending} onClick={() => setDeleting(null)}>Cancel</Button>
          <Button variant="destructive" disabled={remove.isPending} onClick={() => { if (deleting) remove.mutate(deleting.id); }}>Delete group</Button></DialogFooter>
      </DialogContent>
    </Dialog>
  </Card>;
}

function EditorLoader({ id, onClose }: { id: string; onClose: () => void }) {
  const query = useQuery({ queryKey: ["groups", id, "definition"], queryFn: () => groupsApi.definition(id), enabled: id !== "new" });
  if (id === "new") return <GroupEditor initial={emptyGroup()} onClose={onClose} />;
  if (query.isPending) return <Loading />;
  if (query.isError) return <ErrorState onRetry={() => void query.refetch()} />;
  return <GroupEditor id={id} initial={query.data} onClose={onClose} />;
}

function GroupEditor({ id, initial, onClose }: { id?: string; initial: GroupInput; onClose: () => void }) {
  const uid = useId();
  const cache = useQueryClient();
  const [draft, setDraft] = useState(initial);
  const [search, setSearch] = useState("");
  const [offset, setOffset] = useState(0);
  const [previewInput, setPreviewInput] = useState<GroupInput | null>(null);
  const [preview, setPreview] = useState<Awaited<ReturnType<typeof groupsApi.preview>> | null>(null);
  const candidates = useQuery({ queryKey: ["groups", "candidates", draft.catalogType, search, offset], queryFn: () => groupsApi.candidates(draft.catalogType, search, offset), enabled: draft.kind === "manual" });
  const save = useMutation({ mutationFn: () => groupsApi.save(id, draft), onSuccess: () => {
    void cache.invalidateQueries({ queryKey: ["groups"] }); toast.success("Group saved"); onClose();
  } });
  const previewRequest = useMutation({ mutationFn: (input: GroupInput) => groupsApi.preview(input), onSuccess: (data, input) => { setPreview(data); setPreviewInput(input); } });
  const changeRule = (index: number, rule: GroupCondition) => setDraft({ ...draft, rules: { ...draft.rules, conditions: draft.rules.conditions.map((c, i) => i === index ? rule : c) } });
  const busy = save.isPending;
  const previewCurrent = previewInput === draft;
  return <form onSubmit={e => { e.preventDefault(); save.mutate(); }} className="flex flex-col gap-5">
    <fieldset disabled={busy} className="flex min-w-0 flex-col gap-5">
      <label className="flex flex-col gap-2 text-sm font-medium">Name<Input value={draft.name} maxLength={120} required onChange={e => setDraft({ ...draft, name: e.target.value })} /></label>
      <div className="grid gap-4 sm:grid-cols-2">
        <div className="flex flex-col gap-2 text-sm"><span>Catalog type</span><Choice label="Catalog type" value={draft.catalogType} disabled={!!id}
          options={[{ value: "movie", label: "Movies" }, { value: "series", label: "Series" }, { value: "anime", label: "Anime" }]}
          onChange={v => { setDraft({ ...draft, catalogType: v as GroupInput["catalogType"], memberIds: [] }); setOffset(0); setSearch(""); }} /></div>
        <div className="flex flex-col gap-2 text-sm"><span>Group type</span><Choice label="Group type" value={draft.kind} disabled={!!id}
          options={[{ value: "manual", label: "Manual" }, { value: "smart", label: "Smart" }]}
          onChange={v => setDraft({ ...draft, kind: v as GroupInput["kind"], memberIds: [], rules: { version: 1, match: "all", conditions: v === "smart" ? [{ field: "year", operator: "before", value: "2000" }] : [] } })} /></div>
      </div>
      {draft.kind === "smart" ? <section className="flex flex-col gap-3" aria-label="Smart group rules">
        <Choice label="Match conditions" value={draft.rules.match} options={[{ value: "all", label: "All conditions (AND)" }, { value: "any", label: "Any condition (OR)" }]}
          onChange={v => setDraft({ ...draft, rules: { ...draft.rules, match: v as "all" | "any" } })} />
        {draft.rules.conditions.map((rule, index) => <RuleRow key={index} rule={rule} index={index} catalogType={draft.catalogType} uid={uid}
          onChange={r => changeRule(index, r)} onRemove={() => setDraft({ ...draft, rules: { ...draft.rules, conditions: draft.rules.conditions.filter((_, i) => i !== index) } })} />)}
        <Button type="button" variant="outline" className="self-start" disabled={draft.rules.conditions.length >= 20} onClick={() => setDraft({ ...draft, rules: { ...draft.rules, conditions: [...draft.rules.conditions, { field: "year", operator: "before", value: "2000" }] } })}><Plus /> Add condition</Button>
        <p className="text-muted-foreground text-sm">Quality conditions joined with AND must match one source. For series, one matching episode is enough. Each title appears once.</p>
      </section> : <section className="flex flex-col gap-3" aria-label="Manual membership">
        <label className="text-sm">Search {groupTypeLabel(draft.catalogType)}<Input value={search} onChange={e => { setSearch(e.target.value); setOffset(0); }} placeholder="Search titles" /></label>
        <p className="text-muted-foreground text-sm">{draft.memberIds.length} selected across all pages</p>
        {candidates.isPending ? <Loading /> : candidates.isError ? <ErrorState onRetry={() => void candidates.refetch()} /> : <>
          <div className="max-h-64 overflow-y-auto rounded-lg border">{candidates.data.items.length === 0 && <p className="p-3 text-sm">No titles found.</p>}
            {candidates.data.items.map(item => <label key={item.id} className="hover:bg-accent flex cursor-pointer items-center gap-3 border-b p-3 last:border-0">
              <input type="checkbox" checked={draft.memberIds.includes(item.id)} onChange={e => setDraft({ ...draft, memberIds: e.target.checked ? [...draft.memberIds, item.id] : draft.memberIds.filter(member => member !== item.id) })} />
              <span className="text-sm">{item.title}{item.year ? ` (${item.year})` : ""}</span>
            </label>)}
          </div>
          <div className="flex justify-between"><Button type="button" variant="outline" disabled={!offset} onClick={() => setOffset(Math.max(0, offset - 30))}>Previous titles</Button>
            <Button type="button" variant="outline" disabled={offset + candidates.data.items.length >= candidates.data.total} onClick={() => setOffset(offset + 30)}>Next titles</Button></div>
        </>}
      </section>}
      <div className="flex flex-col gap-3">
        <Button type="button" variant="secondary" className="self-start" disabled={previewRequest.isPending || !draft.name.trim()} onClick={() => previewRequest.mutate(draft)}>{previewRequest.isPending ? "Checking…" : "Preview matches"}</Button>
        {previewRequest.isError && <p role="alert" className="text-destructive text-sm">{errorMessage(previewRequest.error)}</p>}
        {preview && previewCurrent && <><p className="text-sm">{count(preview.total, "matching title")}{preview.total > preview.items.length ? ` · First ${preview.items.length} shown` : ""}</p><GroupPosters items={preview.items} /></>}
        {preview && !previewCurrent && <p className="text-muted-foreground text-sm">Rules changed. Preview again to see current matches.</p>}
      </div>
    </fieldset>
    {save.isError && <p role="alert" className="text-destructive text-sm">{errorMessage(save.error)}</p>}
    <DialogFooter><Button type="button" variant="outline" disabled={busy} onClick={onClose}>Cancel</Button><Button type="submit" disabled={busy || !draft.name.trim() || (draft.kind === "smart" && draft.rules.conditions.length === 0)}>{busy ? "Saving…" : "Save group"}</Button></DialogFooter>
  </form>;
}

function RuleRow({ rule, index, catalogType, uid, onChange, onRemove }: { rule: GroupCondition; index: number; catalogType: GroupInput["catalogType"]; uid: string; onChange: (rule: GroupCondition) => void; onRemove: () => void }) {
  const options = useQuery({ queryKey: ["groups", "options", catalogType, rule.field === "tag" || rule.field === "genre" ? rule.value : ""],
    queryFn: () => groupsApi.options(catalogType, rule.field === "tag" || rule.field === "genre" ? rule.value : "") });
  const listId = `${uid}-rule-${index}`;
  const values = rule.field === "hdr" ? options.data?.hdrFormats : rule.field === "resolution" ? options.data?.resolutions : undefined;
  return <div className="flex flex-wrap items-start gap-2 rounded-lg border p-3">
    <div className="w-40"><Choice label={`Condition ${index + 1} field`} value={rule.field} options={fields} onChange={field => onChange({ field: field as GroupCondition["field"], operator: field === "year" ? "before" : field === "tag" || field === "genre" ? "contains" : "equals", value: field === "year" ? "2000" : field === "hdr" ? "any" : field === "resolution" ? "2160p" : "" })} /></div>
    {rule.field === "year" && <div className="w-36"><Choice label={`Condition ${index + 1} operator`} value={rule.operator}
      options={[{ value: "before", label: "Before" }, { value: "after", label: "After" }, { value: "equals", label: "Equals" }, { value: "between", label: "Between" }]}
      onChange={operator => onChange({ ...rule, operator, endValue: operator === "between" ? rule.value : null })} /></div>}
    <div className="min-w-36 flex-1">
      {rule.field === "hdr" || rule.field === "resolution" ? <Choice label={`Condition ${index + 1} value`} value={rule.value} options={(values ?? [rule.value]).map(value => ({ value, label: rule.field === "hdr" ? hdrLabel(value) : value === "2160p" ? "4K (2160p)" : value }))} onChange={value => onChange({ ...rule, value })} />
        : <Input aria-label={`Condition ${index + 1} value`} type={rule.field === "year" ? "number" : "text"} required value={rule.value} list={rule.field === "year" ? undefined : listId} placeholder={rule.field === "tag" ? "Search keyword tags" : "Search genres"} onChange={e => onChange({ ...rule, value: e.target.value })} />}
      {(rule.field === "tag" || rule.field === "genre") && <datalist id={listId}>{(rule.field === "tag" ? options.data?.tags : options.data?.genres)?.map(value => <option key={value} value={value} />)}</datalist>}
      {options.isError && <p className="text-destructive mt-1 text-xs">Could not load choices. <button type="button" onClick={() => void options.refetch()}>Retry</button></p>}
    </div>
    {rule.field === "year" && rule.operator === "between" && <Input className="w-28" type="number" required aria-label={`Condition ${index + 1} end year`} value={rule.endValue ?? ""} onChange={e => onChange({ ...rule, endValue: e.target.value })} />}
    <Button type="button" variant="ghost" aria-label={`Remove condition ${index + 1}`} onClick={onRemove}><Trash2 /></Button>
  </div>;
}
