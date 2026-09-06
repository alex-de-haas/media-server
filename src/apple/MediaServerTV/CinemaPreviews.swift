#if DEBUG
import Foundation
import HTTPTypes
import MediaKit
import OpenAPIRuntime
import SwiftUI

/// Local-only fixtures for previews and simulator focus checks; no server or stored identity is used.
private struct CinemaPreviewTransport: ClientTransport {
    func send(_ request: HTTPRequest, body: HTTPBody?, baseURL: URL, operationID: String)
        async throws -> (HTTPResponse, HTTPBody?) {
        let path = request.path ?? ""
        let titles = ["The Long Journey Across the Silent Northern Sea", "A Summer in the City", "The Last Lighthouse"]
        func card(_ index: Int) -> String {
            """
            {"id":"movie-\(index)","catalogId":"films","kind":"Movie","title":"\(titles[index])","year":\(2001 + index),"userData":{"key":"\(index)","playbackPositionTicks":\(index == 0 ? 25350000000 : 0),"playCount":0,"isFavorite":false,"played":false}}
            """
        }
        let cards = (0..<3).map(card).joined(separator: ",")
        let json: String
        if path.contains("/collections/saga") {
            json = """
            {"id":"saga","name":"The Northern Sea Collection","items":[\(cards)]}
            """
        } else if path.contains("/collections") {
            json = #"[{"id":"saga","name":"The Northern Sea Collection","itemCount":3}]"#
        } else if path.contains("/items/") {
            let index = Int(path.split(separator: "-").last ?? "0") ?? 0
            json = """
            {"detail":{"id":"movie-\(index)","catalogId":"films","catalogName":"Films","catalogRoot":"/films","kind":"Movie","title":"\(titles[min(index, 2)])","year":2001,"runtimeTicks":72000000000,"overview":"A lighthouse keeper discovers a letter that draws her across the northern coast. As the seasons change, each village offers another piece of the story. An intimate journey through memory, friendship, and the places we call home. The long description continues so the expanded synopsis can be checked on a television.","genres":["Drama","Adventure"],"mediaSources":[],"cast":[],"directors":[],"creators":[],"studios":[],"keywords":[],"userData":{"key":"0","playbackPositionTicks":25350000000,"playCount":0,"isFavorite":false,"played":false}},"sources":[],"images":{}}
            """
        } else {
            json = """
            {"items":[\(cards)],"removedIds":[],"changedPreferenceScopes":[],"cursor":"preview","hasMore":false,"resetRequired":false}
            """
        }
        var response = HTTPResponse(status: .ok)
        response.headerFields[.contentType] = "application/json"
        return (response, HTTPBody(json))
    }
}

struct CinemaPreview: View {
    @State private var session = ServerSession(
        paired: PairedServer(server: URL(string: "https://preview.invalid")!, serverName: "Preview Library",
                             appId: "preview", coreOrigin: URL(string: "https://preview.invalid")!, coreToken: "",
                             identity: AppIdentity(accessToken: "", expiresAt: .distantFuture)),
        store: InMemoryCredentialStore(), transport: CinemaPreviewTransport())
    @State private var pairing = PairingSession(store: InMemoryCredentialStore())

    var body: some View {
        LibraryView(session: session, pairing: pairing)
            .preferredColorScheme(ProcessInfo.processInfo.arguments.contains("--preview-light") ? .light : nil)
    }
}

#Preview("Library · long titles and missing artwork") { CinemaPreview() }
#Preview("Library · light appearance") { CinemaPreview().preferredColorScheme(.light) }
#Preview("Library · dark appearance") { CinemaPreview().preferredColorScheme(.dark) }
#Preview("Backdrop · no artwork") {
    CinemaBackdrop(url: nil, loader: ArtworkLoader(token: { nil }))
}
#endif
