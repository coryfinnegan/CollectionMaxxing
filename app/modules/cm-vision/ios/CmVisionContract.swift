import CoreGraphics
import Foundation

// The result shapes. Keys must match ../contract.schema.json; a Jest test and
// scripts/check-contract.js read the `// contract:` blocks below and fail on any difference.
enum CmContract {
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
}
