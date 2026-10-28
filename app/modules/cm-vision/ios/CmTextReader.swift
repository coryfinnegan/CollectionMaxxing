import Foundation
import FoundationModels
import os

// Spike #505: the same on-device model answers from OCR text, with no picture attached. The
// instructions arrive from TypeScript in `options.prompt` and pass through untouched, the OCR text
// goes in as plain text, and the model answers in the shared typed shape from CmGenerable.swift.
// Text input is iOS 26 API, so this runs where the picture read cannot. Logs carry counts and
// durations only.

@available(iOS 26.0, *)
enum CmTextReader {
  private static let logger = Logger(subsystem: Bundle.main.bundleIdentifier ?? "cm-vision", category: "model")

  /// Text-model availability. Guided generation is core API from iOS 26, so an available model can
  /// extract; vision stays false here (the iOS 27 branch reports it). The context size stays unknown:
  /// TypeScript treats a null size as unknown.
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
    return CmContract.modelCapabilities(
      available: available,
      vision: false,
      guidedGeneration: available,
      contextSize: nil,
      reason: reason
    )
  }

  /// One streamed extraction. Always returns a ReadItemsResult dictionary; it never throws.
  static func extract(
    ocrText: String, request: CmReadRequest, emit: @escaping ([String: Any]) -> Void
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
      logger.info("extract \(name, privacy: .public) n=\(count) skipped=\(skipped) ms=\(ms)")
      return CmContract.readItemsResult(
        items: read.items, unreadableCount: read.unreadable, outcome: outcome.rawValue, elapsedMs: elapsedMs)
    }

    guard case .available = SystemLanguageModel.default.availability else {
      return finish(.unavailable)
    }

    let gate = CmOutcomeGate()
    let worker = Task.detached(priority: .userInitiated) {
      gate.resolve(await consume(ocrText: ocrText, prompt: request.prompt, run: run))
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

  private static func consume(ocrText: String, prompt: String, run: CmReadRun) async -> CmOutcome {
    do {
      let session = LanguageModelSession(instructions: prompt)
      let stream = session.streamResponse(generating: CmGenRead.self) {
        ocrText
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
