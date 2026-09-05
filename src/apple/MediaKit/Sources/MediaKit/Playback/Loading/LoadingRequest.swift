import AVFoundation

/// The request operations used by the loader, so its delivery lifecycle can be exercised without
/// constructing AVFoundation-owned requests. All access occurs on the loader's serial queue.
protocol LoadingDataRequest: AnyObject, Sendable {
    var currentOffset: Int64 { get }
    var requestedOffset: Int64 { get }
    var requestedLength: Int { get }
    var requestsAllDataToEndOfResource: Bool { get }
    func respond(with data: Data)
}

protocol LoadingRequest: AnyObject, Sendable {
    var loadingData: (any LoadingDataRequest)? { get }
    func describe(length: Int64)
    func finishLoading()
    func finishLoading(with error: (any Error)?)
}

extension AVAssetResourceLoadingDataRequest: LoadingDataRequest {}

extension AVAssetResourceLoadingRequest: LoadingRequest {
    var loadingData: (any LoadingDataRequest)? { dataRequest }

    func describe(length: Int64) {
        guard let information = contentInformationRequest else { return }
        information.contentType = "public.mpeg-4"
        information.contentLength = length
        information.isByteRangeAccessSupported = true
    }
}
