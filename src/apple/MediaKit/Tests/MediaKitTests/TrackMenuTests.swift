import Foundation
import MediaServerAPI
import Testing
@testable import MediaKit

@Suite("Track menu descriptions")
struct TrackMenuTests {
    private let english = Locale(identifier: "en")

    private func track(
        language: String? = nil, title: String? = nil,
        codec: String? = nil, channels: Int32? = nil
    ) -> TitleTrack {
        TitleTrack(Components.Schemas.MediaStreamDto(
            id: "track", _type: "Audio", index: 1, codec: codec, language: language,
            title: title, channels: channels, isDefault: false, isForced: false, isExternal: false))
    }

    @Test("A named dub retains its name alongside language, codec and channel count")
    func namedAudio() {
        let audio = track(language: "rus", title: "Dub, TVShows", codec: "eac3", channels: 6)
        #expect(audio.menuTitle(locale: english) == "Russian · Dub, TVShows")
        #expect(audio.audioMenuDetails == "E-AC-3 · 6 ch")
        #expect(audio.channels == 6)
    }

    @Test("Identically titled subtitles are distinguished by language")
    func subtitles() {
        #expect(track(language: "rus", title: "full").menuTitle(locale: english) == "Russian · full")
        #expect(track(language: "eng", title: "full").menuTitle(locale: english) == "English · full")
    }

    @Test("Untitled tracks do not repeat the legacy language-and-codec fallback")
    func untitled() {
        let audio = track(language: "eng", codec: "aac", channels: 2)
        #expect(audio.menuTitle(locale: english) == "English")
        #expect(audio.audioMenuDetails == "AAC · 2 ch")
        #expect(track(language: "en", title: "English").menuTitle(locale: english) == "English")
    }

    @Test("Missing or invalid metadata does not invent a codec or channel layout")
    func missing() {
        #expect(track(title: "forced").menuTitle(locale: english) == "Unknown language · forced")
        #expect(track(language: "und", title: " ").menuTitle(locale: english) == "Unknown language")
        #expect(track(codec: " ", channels: 0).audioMenuDetails == nil)
        #expect(track(channels: -1).audioMenuDetails == nil)
        #expect(track(codec: "ac3").audioMenuDetails == "AC-3")
        #expect(track(channels: 8).audioMenuDetails == "8 ch")
    }

    @Test("Language names follow the display locale")
    func localizedLanguage() {
        #expect(track(language: "en").menuTitle(locale: Locale(identifier: "ru")) == "английский")
    }
}
