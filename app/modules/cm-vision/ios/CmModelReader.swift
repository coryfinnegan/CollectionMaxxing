import Foundation
import FoundationModels
import os

// Apple's on-device model, called as thinly as possible: the instructions arrive from TypeScript in
// `options.prompt` and are passed through untouched, the picture goes in with
// `Attachment(cgImage, orientation:)`, and the model answers in the shared typed shape from
// CmGenerable.swift, which mirrors the contract's ModelItem. Nothing here decides what counts as a
// match; that stays in TypeScript and on the server. Image input needs iOS 27, so everything is gated
// on it. Logs carry counts and durations only.

@available(iOS 27.0, *)
enum CmModelReader {
  private static let logger = Logger(subsystem: Bundle.main.bundleIdentifier ?? "cm-vision", category: "model")

  static func capabilities() -> [String: Any] {
    let model = SystemLanguageModel.default
    var available = false
    var reason: String?
    switch model.availability {
    case .available:
      available = true
    case .unavailable(let why):
      switch why {
      case .deviceNotEligible: reason = "notSupported"
      case .appleIntelligenceNotEnabled: reason = "appleIntelligenceOff"
      case .modelNotReady: reason = "modelNotReady"
      @unknown default: reason = "notSupported"
      }
    }
    let capabilities = model.capabilities
    return CmContract.modelCapabilities(
      available: available,
      vision: available && capabilities.contains(.vision),
      guidedGeneration: available && capabilities.contains(.guidedGeneration),
      contextSize: available ? model.contextSize : nil,
      reason: reason
    )
  }

  /// One streamed model call. Always returns a ReadItemsResult dictionary; it never throws.
  static func read(
    imageUri: String, request: CmReadRequest, emit: @escaping ([String: Any]) -> Void
  ) async -> [String: Any] {
    let clock = ContinuousClock()
    let started = clock.now
    let run = CmReadRun(maxItems: request.maxItems, emit: emit)

    func finish(_ outcome: CmOutcome) -> [String: Any] {
      let read = run.close()
      let parts = (clock.now - started).components
      let elapsedMs = Double(parts.seconds) * 1000 + Double(parts.attoseconds) / 1_000_000_000_000_000
      let name = outcome.rawValue
      let count = read.items.count
      let skipped = read.unreadable
      let ms = Int(elapsedMs)
      logger.info("read \(name, privacy: .public) n=\(count) skipped=\(skipped) ms=\(ms)")
      return CmContract.readItemsResult(
        items: read.items, unreadableCount: read.unreadable, outcome: outcome.rawValue, elapsedMs: elapsedMs)
    }

    let model = SystemLanguageModel.default
    guard case .available = model.availability, model.capabilities.contains(.vision) else {
      return finish(.unavailable)
    }
    guard let image = try? CmImage.load(imageUri) else {
      return finish(.unavailable)
    }

    let gate = CmOutcomeGate()
    let worker = Task.detached(priority: .userInitiated) {
      gate.resolve(await consume(image: image, prompt: request.prompt, run: run))
    }
    let timer = Task.detached {
      let nanoseconds = UInt64(min(max(request.timeoutMs, 0), 600_000) * 1_000_000)
      try? await Task.sleep(nanoseconds: nanoseconds)
      if !Task.isCancelled { gate.resolve(.timeout) }
    }
    let outcome = await gate.wait()
    worker.cancel()
    timer.cancel()
    return finish(outcome)
  }

  private static func consume(image: CmLoadedImage, prompt: String, run: CmReadRun) async -> CmOutcome {
    do {
      let session = LanguageModelSession(instructions: prompt)
      let stream = session.streamResponse(generating: CmGenRead.self) {
        "Picture:"
        Attachment(image.cgImage, orientation: image.orientation)
      }
      var latest: [CmGenItem.PartiallyGenerated] = []
      var next = 0
      for try await snapshot in stream {
        try Task.checkCancellation()
        latest = snapshot.content.items ?? []
        // The last item may still be growing; every earlier one is finished.
        while next < latest.count - 1 {
          CmGeneration.add(latest[next], to: run)
          next += 1
        }
        if run.isFull { return .ok }
      }
      while next < latest.count {
        CmGeneration.add(latest[next], to: run)
        next += 1
      }
      return .ok
    } catch is CancellationError {
      return .unavailable
    } catch let error as LanguageModelSession.GenerationError {
      switch error {
      case .exceededContextWindowSize: return .contextExceeded
      case .guardrailViolation, .refusal: return .declined
      default: return .unavailable
      }
    } catch {
      return .unavailable
    }
  }
}
