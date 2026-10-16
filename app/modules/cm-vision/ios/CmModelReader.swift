import Foundation
import FoundationModels
import os

// Apple's on-device model, called as thinly as possible: the instructions arrive from TypeScript in
// `options.prompt` and are passed through untouched, the picture goes in with
// `Attachment(cgImage, orientation:)`, and the model answers in the `@Generable` shape below, which mirrors
// the contract's ModelItem. Nothing here decides what counts as a match; that stays in TypeScript and on the server.
// Image input needs iOS 27, so everything is gated on it. Logs carry counts and durations only.

enum CmOutcome: String {
  case ok, declined, timeout, contextExceeded, unavailable
}

/// What the module hands to a read: parsed from the options dictionary TypeScript sent.
struct CmReadRequest {
  let prompt: String
  let timeoutMs: Double
  let maxItems: Int?

  init?(_ options: [String: Any]) {
    guard let prompt = options["prompt"] as? String, !prompt.isEmpty,
          let timeoutMs = (options["timeoutMs"] as? NSNumber)?.doubleValue else { return nil }
    self.prompt = prompt
    self.timeoutMs = timeoutMs
    if let max = (options["maxItems"] as? NSNumber)?.intValue, max > 0 {
      self.maxItems = max
    } else {
      self.maxItems = nil
    }
  }
}

/// Resolves exactly once with the first outcome offered (the stream finishing, or the timer).
final class CmOutcomeGate: @unchecked Sendable {
  private let lock = NSLock()
  private var outcome: CmOutcome?
  private var waiter: CheckedContinuation<CmOutcome, Never>?

  func resolve(_ value: CmOutcome) {
    lock.lock()
    defer { lock.unlock() }
    guard outcome == nil else { return }
    outcome = value
    waiter?.resume(returning: value)
    waiter = nil
  }

  func wait() async -> CmOutcome {
    await withCheckedContinuation { continuation in
      lock.lock()
      defer { lock.unlock() }
      if let outcome = outcome {
        continuation.resume(returning: outcome)
      } else {
        waiter = continuation
      }
    }
  }
}

/// The items read so far. Once closed (the read has resolved) nothing more is recorded or emitted.
final class CmReadRun: @unchecked Sendable {
  private let lock = NSLock()
  private let maxItems: Int?
  private let emit: ([String: Any]) -> Void
  private var items: [[String: Any]] = []
  private var unreadable = 0
  private var closed = false

  init(maxItems: Int?, emit: @escaping ([String: Any]) -> Void) {
    self.maxItems = maxItems
    self.emit = emit
  }

  var isFull: Bool {
    lock.lock()
    defer { lock.unlock() }
    guard let maxItems = maxItems else { return false }
    return items.count >= maxItems
  }

  /// Records a finished item (or counts it as unreadable when a required field is missing) and streams it.
  func add(_ build: (_ order: Int) -> [String: Any]?) {
    lock.lock()
    if closed || (maxItems.map { items.count >= $0 } ?? false) {
      lock.unlock()
      return
    }
    guard let item = build(items.count) else {
      unreadable += 1
      lock.unlock()
      return
    }
    items.append(item)
    lock.unlock()
    emit(item)
  }

  func close() -> (items: [[String: Any]], unreadable: Int) {
    lock.lock()
    defer { lock.unlock() }
    closed = true
    return (items, unreadable)
  }
}

@available(iOS 27.0, *)
@Generable
enum CmGenKind {
  case book, vhs, dvd, bluray, uhdBluray, game, other

  var contractName: String {
    switch self {
    case .book: return "book"
    case .vhs: return "vhs"
    case .dvd: return "dvd"
    case .bluray: return "bluray"
    case .uhdBluray: return "uhdBluray"
    case .game: return "game"
    case .other: return "other"
    }
  }
}

@available(iOS 27.0, *)
@Generable
enum CmGenCompleteness {
  case looseCart, discOnly, boxed, manualVisible

  var contractName: String {
    switch self {
    case .looseCart: return "looseCart"
    case .discOnly: return "discOnly"
    case .boxed: return "boxed"
    case .manualVisible: return "manualVisible"
    }
  }
}

@available(iOS 27.0, *)
@Generable
enum CmGenConfidence {
  case high, medium, low

  var contractName: String {
    switch self {
    case .high: return "high"
    case .medium: return "medium"
    case .low: return "low"
    }
  }
}

/// Field order is generation order: the title streams first so a card can appear early.
@available(iOS 27.0, *)
@Generable
struct CmGenItem {
  var title: String
  var creator: String?
  var kind: CmGenKind
  var platform: String?
  var editionMarks: String?
  var completeness: CmGenCompleteness?
  var visibleText: String
  var confidence: CmGenConfidence
}

@available(iOS 27.0, *)
@Generable
struct CmGenRead {
  var items: [CmGenItem]
}

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
          add(latest[next], to: run)
          next += 1
        }
        if run.isFull { return .ok }
      }
      while next < latest.count {
        add(latest[next], to: run)
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

  private static func add(_ partial: CmGenItem.PartiallyGenerated, to run: CmReadRun) {
    run.add { order in
      guard let title = partial.title?.trimmingCharacters(in: .whitespacesAndNewlines), !title.isEmpty,
            let kind = partial.kind, let confidence = partial.confidence else { return nil }
      return CmContract.modelItem(
        visibleText: partial.visibleText ?? "",
        title: title,
        creator: partial.creator,
        kind: kind.contractName,
        platform: partial.platform,
        editionMarks: partial.editionMarks,
        completeness: partial.completeness?.contractName,
        order: order,
        confidence: confidence.contractName
      )
    }
  }
}
