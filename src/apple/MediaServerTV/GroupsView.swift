import MediaKit
import SwiftUI

/// Folder browsing, separate from provider-managed franchise collections.
struct GroupsView: View {
    let session: ServerSession
    let library: LibraryStore
    @State private var store: GroupStore

    init(session: ServerSession, library: LibraryStore) {
        self.session = session
        self.library = library
        _store = State(initialValue: GroupStore(session: session))
    }

    var body: some View {
        Group {
            switch store.state {
            case .loading: ProgressView("Reading groups")
            case .unsupported:
                VStack(spacing: 24) {
                    ContentUnavailableView("Update your server", systemImage: "arrow.up.circle",
                        description: Text("This server does not support groups yet."))
                    Button("Try again") { Task { await store.load() } }
                }
            case .failed(let reason):
                VStack(spacing: 24) {
                    Text("Could not read groups").font(.title)
                    Text(reason).foregroundStyle(.secondary)
                    Button("Try again") { Task { await store.load() } }
                }
            case .loaded:
                if store.items.isEmpty {
                    ContentUnavailableView("No groups yet", systemImage: "folder",
                        description: Text("Create groups in the web app under Settings → Groups."))
                } else {
                    ScrollView {
                        VStack(alignment: .leading, spacing: 44) {
                            Text("Groups").font(.largeTitle.bold())
                            LazyVGrid(columns: [GridItem(.adaptive(minimum: 360), spacing: 40)], spacing: 40) {
                                ForEach(store.items) { group in
                                    NavigationLink {
                                        GroupDetailView(id: group.id, store: store, session: session, library: library)
                                    } label: {
                                        VStack(alignment: .leading, spacing: 16) {
                                            Image(systemName: "folder.fill").font(.system(size: 60)).foregroundStyle(.secondary)
                                            Text(group.name).font(.headline).lineLimit(2)
                                            Text("\(group.typeLabel) · \(group.itemCount) \(group.itemCount == 1 ? "title" : "titles")")
                                                .font(.caption).foregroundStyle(.secondary)
                                        }.frame(maxWidth: .infinity, minHeight: 190, alignment: .leading).padding(28)
                                    }.buttonStyle(.card)
                                }
                            }
                        }.padding(CinemaStyle.inset)
                    }
                }
            }
        }.task { await store.load() }
    }
}

private struct GroupDetailView: View {
    let id: String
    let store: GroupStore
    let session: ServerSession
    let library: LibraryStore
    @State private var page: LibraryGroupPage?
    @State private var offset = 0
    @State private var failure: String?
    @State private var missing = false

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 44) {
                if missing {
                    ContentUnavailableView("Group no longer available", systemImage: "folder.badge.questionmark",
                        description: Text("Return to Groups to see the current list."))
                } else if let failure {
                    Text("Could not open this group").font(.title)
                    Text(failure).foregroundStyle(.secondary)
                    Button("Try again") { Task { await load() } }
                } else if let page {
                    VStack(alignment: .leading, spacing: 12) {
                        Text(page.name).font(.largeTitle.bold())
                        Text("\(page.total) \(page.total == 1 ? "title" : "titles")").foregroundStyle(.secondary)
                    }
                    if page.items.isEmpty {
                        Text("No matching titles on this page.").foregroundStyle(.secondary)
                    } else {
                        LibraryPosterGrid(items: page.items, library: library, loader: session.artwork,
                                          playback: PlaybackService(session: session))
                    }
                    if offset > 0 || page.hasNext {
                        HStack(spacing: 30) {
                            Button("Previous") { offset = max(0, offset - page.limit) }.disabled(offset == 0)
                            Text("Page \(offset / max(1, page.limit) + 1)").foregroundStyle(.secondary)
                            Button("Next") { offset += page.limit }.disabled(!page.hasNext)
                        }
                    }
                } else { ProgressView("Reading group") }
            }.padding(CinemaStyle.inset)
        }.task(id: offset) { await load() }
    }

    private func load() async {
        page = nil; failure = nil; missing = false
        do {
            let result = try await store.page(id: id, offset: offset)
            guard !Task.isCancelled else { return }
            page = result
        } catch GroupError.missing { missing = true }
        catch { if !Task.isCancelled { failure = error.localizedDescription } }
    }
}
