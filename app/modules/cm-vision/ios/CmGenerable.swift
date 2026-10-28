import Foundation
import FoundationModels

// Shared pieces for the two model calls: the picture read (CmModelReader, iOS 27) and the OCR-text
// extraction (CmTextReader, spike #505, iOS 26). Everything here is plain Swift plus the framework's
// typed output, which is why it is available from iOS 26: only image input needs iOS 27.

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

@available(iOS 26.0, *)
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

@available(iOS 26.0, *)
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

@available(iOS 26.0, *)
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
@available(iOS 26.0, *)
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

@available(iOS 26.0, *)
@Generable
struct CmGenRead {
  var items: [CmGenItem]
}

/// Turns finished generated items into contract dictionaries. Both readers call it; neither decides
/// what counts as a match, that stays in TypeScript and on the server.
@available(iOS 26.0, *)
enum CmGeneration {
  static func add(_ partial: CmGenItem.PartiallyGenerated, to run: CmReadRun) {
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
