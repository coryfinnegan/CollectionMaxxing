import Foundation
import FoundationModels
import os

// Apple's on-device model, called as thinly as possible: the instructions arrive from TypeScript in
// `options.prompt` and are passed through untouched, the picture goes in with
// `Attachment(cgImage, orientation:)`, and the model answers in the shared typed shape from
// CmGenerable.swift, which mirrors the contract's ModelItem. Nothing here decides what counts as a
// match; that stays in TypeScript and on the server. Image input needs iOS 27, so everything is gated
// on it. Logs carry counts and durations only.

/// Whether this read started from a warmed session; for the log line only.
final class CmWarmUse: @unchecked Sendable {
  private let lock = NSLock()
  private var warm = false

  func record(_ value: Bool) {
    lock.lock()
    defer { lock.unlock() }
    warm = value
  }

  var wasWarm: Bool {
    lock.lock()
    defer { lock.unlock() }
    return warm
  }
}

/// Holds at most one prewarmed session for the next read. A read takes it out of the slot, so a session
/// answers one read only (its transcript would otherwise grow until the context overflows).
@available(iOS 27.0, *)
final class CmWarmSlot: @unchecked Sendable {
  private let lock = NSLock()
  private var instructions: String?
  private var session: LanguageModelSession?

  /// The warmed session if it was built for these exact instructions; otherwise nil. Either way the slot is empty after.
  func take(for wanted: String) -> LanguageModelSession? {
    lock.lock()
    defer { lock.unlock() }
    let held = session
    let matches = instructions == wanted
    session = nil
    instructions = nil
    return matches ? held : nil
  }

  /// Builds a fresh session with the same instructions and asks the framework to load it ahead of time.
  func warm(for prompt: String) {
    let next = LanguageModelSession(instructions: prompt)
    next.prewarm()
    lock.lock()
    defer { lock.unlock() }
    session = next
    instructions = prompt
  }
}

@available(iOS 27.0, *)
enum CmModelReader {
  private static let logger = Logger(subsystem: Bundle.main.bundleIdentifier ?? "cm-vision", category: "model")
  private static let warmSlot = CmWarmSlot()

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
    let warmUse = CmWarmUse()

    func finish(_ outcome: CmOutcome) -> [String: Any] {
      let read = run.close()
      let parts = (clock.now - started).components
      let elapsedMs = Double(parts.seconds) * 1000 + Double(parts.attoseconds) / 1_000_000_000_000_000
      let name = outcome.rawValue
      let count = read.items.count
      let skipped = read.unreadable
      let ms = Int(elapsedMs)
      let warm = warmUse.wasWarm ? 1 : 0
      logger.info("read \(name, privacy: .public) n=\(count) skip=\(skipped) warm=\(warm) ms=\(ms)")
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
      let spine = request.spine
      // A warmed session answers this one read; with none (or other instructions) a fresh one is built.
      let warmed = warmSlot.take(for: request.prompt)
      warmUse.record(warmed != nil)
      let session = warmed ?? LanguageModelSession(instructions: request.prompt)
      gate.resolve(await consume(session: session, image: image, spine: spine, run: run))
      // The session that just answered is never reused; the next read gets a new, prewarmed one.
      warmSlot.warm(for: request.prompt)
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

  /// TypeScript asks for the lean spine shape on a shelf of spines; every other read is the full item.
  private static func consume(
    session: LanguageModelSession, image: CmLoadedImage, spine: Bool, run: CmReadRun
  ) async -> CmOutcome {
    do {
      if spine {
        return try await streamSpines(session: session, image: image, run: run)
      }
      return try await streamFull(session: session, image: image, run: run)
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

  private static func streamSpines(
    session: LanguageModelSession, image: CmLoadedImage, run: CmReadRun
  ) async throws -> CmOutcome {
    let stream = session.streamResponse(generating: CmGenSpineRead.self) {
      "Picture:"
      Attachment(image.cgImage, orientation: image.orientation)
    }
    var latest: [CmGenSpine.PartiallyGenerated] = []
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
  }

  private static func streamFull(
    session: LanguageModelSession, image: CmLoadedImage, run: CmReadRun
  ) async throws -> CmOutcome {
    let stream = session.streamResponse(generating: CmGenRead.self) {
      "Picture:"
      Attachment(image.cgImage, orientation: image.orientation)
    }
    var latest: [CmGenItem.PartiallyGenerated] = []
    var next = 0
    for try await snapshot in stream {
      try Task.checkCancellation()
      latest = snapshot.content.items ?? []
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
  }
}
