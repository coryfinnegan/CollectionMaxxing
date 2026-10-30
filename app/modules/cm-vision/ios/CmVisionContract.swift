import CoreGraphics
import Foundation

// The result shapes. Keys must match ../contract.schema.json; a Jest test and
// scripts/check-contract.js read the `// contract:` blocks below and fail on any difference.
// Optional keys are added by assignment (item["key"] = value) so an absent value stays absent.
// Keep this file to builders only: the check reads every line after a marker up to the next one.
enum CmContract {
  /// A box read from a picture that was turned `degrees` clockwise before reading, mapped back to the
  /// upright picture. Input and output are Vision boxes (normalised, bottom-left origin).
  static func unrotated(_ box: CGRect, degrees: Int) -> CGRect {
    // Work in top-left space, where a clockwise quarter turn is a plain permutation of the axes.
    let x = Double(box.minX)
    let y = 1 - Double(box.maxY)
    let w = Double(box.width)
    let h = Double(box.height)
    let upright: (x: Double, y: Double, w: Double, h: Double)
    switch degrees {
    case 90: upright = (x: y, y: 1 - (x + w), w: h, h: w)
    case 180: upright = (x: 1 - (x + w), y: 1 - (y + h), w: w, h: h)
    case 270: upright = (x: 1 - (y + h), y: x, w: h, h: w)
    default: upright = (x: x, y: y, w: w, h: h)
    }
    return CGRect(x: upright.x, y: 1 - (upright.y + upright.h), width: upright.w, height: upright.h)
  }

  // contract: Rect
  /// Vision boxes are normalised with a bottom-left origin; the contract is top-left.
  static func rect(_ r: CGRect) -> [String: Any] {
    return [
      "x": Double(r.minX),
      "y": Double(1 - r.maxY),
      "width": Double(r.width),
      "height": Double(r.height),
    ]
  }

  // contract: TextLine
  static func textLine(text: String, confidence: Float, box: CGRect) -> [String: Any] {
    return [
      "text": text,
      "confidence": Double(confidence),
      "box": rect(box),
    ]
  }

  // contract: FeaturePrint
  static func featurePrint(revision: Int, data: Data) -> [String: Any] {
    return [
      "revision": revision,
      "data": data.base64EncodedString(),
    ]
  }

  // contract: DetectedCode
  static func detectedCode(value: String, symbology: String, bounds: CGRect) -> [String: Any] {
    return [
      "value": value,
      "symbology": symbology,
      "bounds": rect(bounds),
    ]
  }

  // contract: ModelItem
  static func modelItem(
    visibleText: String, title: String, creator: String?, kind: String, platform: String?,
    editionMarks: String?, completeness: String?, order: Int, confidence: String
  ) -> [String: Any] {
    var item: [String: Any] = [
      "visibleText": visibleText,
      "title": title,
      "kind": kind,
      "order": order,
      "confidence": confidence,
    ]
    if let creator = creator { item["creator"] = creator }
    if let platform = platform { item["platform"] = platform }
    if let editionMarks = editionMarks { item["editionMarks"] = editionMarks }
    if let completeness = completeness { item["completeness"] = completeness }
    return item
  }

  // contract: ModelCapabilities
  static func modelCapabilities(
    available: Bool, vision: Bool, guidedGeneration: Bool, contextSize: Int?, reason: String?
  ) -> [String: Any] {
    var capabilities: [String: Any] = [
      "available": available,
      "vision": vision,
      "guidedGeneration": guidedGeneration,
      "contextSize": contextSize.map { $0 as Any } ?? NSNull(),
    ]
    if let reason = reason { capabilities["reason"] = reason }
    return capabilities
  }

  // contract: ReadItemsResult
  static func readItemsResult(
    items: [[String: Any]], unreadableCount: Int, outcome: String, elapsedMs: Double
  ) -> [String: Any] {
    return [
      "items": items,
      "unreadableCount": unreadableCount,
      "outcome": outcome,
      "elapsedMs": elapsedMs,
    ]
  }

  // contract: DeviceStatus
  static func deviceStatus(thermalState: String, osVersion: String) -> [String: Any] {
    return [
      "thermalState": thermalState,
      "osVersion": osVersion,
    ]
  }
}
