import MediaKit
import SwiftUI

/// Getting a television signed in without a keyboard, a browser, or a password on screen.
///
/// The viewer types one address. Everything after that is a code big enough to read from a sofa and a
/// wait — the approving happens on a phone, in Shell, where a human already is.
struct PairingView: View {
    let session: PairingSession

    @State private var address = ""

    var body: some View {
        VStack(spacing: 48) {
            switch session.state {
            case .idle:
                addressEntry
            case .checking:
                ProgressView("Looking for a server")
            case .awaitingApproval(let grant, let serverName):
                approval(grant, serverName: serverName)
            case .paired:
                ProgressView()
            case .failed(let error):
                failure(error)
            }
        }
        .padding(80)
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private var addressEntry: some View {
        VStack(alignment: .leading, spacing: 24) {
            Text("Media Server")
                .font(.largeTitle)
            Text("Enter the address of your server.")
                .foregroundStyle(.secondary)

            TextField("media.example.com", text: $address)
                .textContentType(.URL)
                // The on-screen keyboard capitalises and corrects by default, which turns a hostname
                // into something that resolves nowhere and gives the viewer nothing to look at.
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .frame(maxWidth: 900)

            Button("Continue") { session.start(address: address) }
                .disabled(address.trimmingCharacters(in: .whitespaces).isEmpty)
        }
    }

    private func approval(_ grant: DeviceCodeGrant, serverName: String) -> some View {
        VStack(spacing: 32) {
            Text(serverName)
                .font(.title2)
                .foregroundStyle(.secondary)

            Text("Approve this device")
                .font(.largeTitle)

            // The whole point of the screen. Monospaced and enormous, because it is read across a room
            // and typed on a different device.
            Text(grant.userCode)
                .font(.system(size: 120, weight: .semibold, design: .monospaced))

            if let uri = grant.verificationUri {
                Text(uri)
                    .font(.title3)
                    .foregroundStyle(.secondary)
            } else {
                // Null when the host runs no Shell. Sending the viewer to an address Core invented
                // would be worse than telling them where to look.
                Text("Open your Hosty host, then Settings → Access tokens.")
                    .font(.title3)
                    .foregroundStyle(.secondary)
            }

            ProgressView()
                .padding(.top, 16)

            Button("Cancel") { session.cancel() }
        }
        // A poll that outlives its screen is a device quietly asking to be signed in while nobody is
        // looking at it.
        .onDisappear { session.cancel() }
    }

    private func failure(_ error: PairingError) -> some View {
        VStack(spacing: 24) {
            Text(title(for: error))
                .font(.title)
            Text(detail(for: error))
                .font(.title3)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
                .frame(maxWidth: 1100)

            Button("Try again") { session.cancel() }
        }
    }

    /// Each failure says which one it is. "Could not sign in" is the answer that helps nobody.
    private func title(for error: PairingError) -> String {
        switch error {
        case .notAMediaServer: "That address is not a Media Server"
        case .noCoreOrigin: "This server cannot be paired"
        case .credentialRejected: "This device is no longer signed in"
        case .unreachable: "Could not reach that address"
        case .coreTooOld: "This host needs updating"
        case .throttled: "Too many pending requests"
        case .codeExpired: "The code expired"
        case .denied: "The request was declined"
        case .notAssigned: "This account has no access"
        case .server: "The server refused"
        }
    }

    private func detail(for error: PairingError) -> String {
        switch error {
        case .notAMediaServer:
            "Something answered, but not a Media Server. Check the address and the port."
        case .noCoreOrigin:
            "The server did not say where its Hosty host is, so there is nowhere to approve this device. Set a public origin for the host and try again."
        case .credentialRejected:
            "The host no longer accepts this device's credential. Pair it again."
        case .unreachable(let reason):
            reason
        case .coreTooOld:
            "Pairing a device needs Hosty 0.73.0 or later. Update the host and try again."
        case .throttled:
            "The host is already holding several pending requests from here. Wait a minute and try again."
        case .codeExpired:
            "Nobody approved it in time. Starting again gives you a new code."
        case .denied:
            "Somebody declined this device in Shell."
        case .notAssigned:
            "The account that approved this device is not assigned to Media Server. Assign it in Shell, then pair again."
        case .server(let code, let message):
            message.isEmpty ? code : message
        }
    }
}

#Preview {
    PairingView(session: PairingSession(store: InMemoryCredentialStore()))
}
