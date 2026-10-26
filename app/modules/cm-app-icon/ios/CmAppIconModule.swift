import ExpoModulesCore
import UIKit

// Switches the home screen icon between the default dragon and the registered
// alternates (CFBundleAlternateIcons, added by plugins/withAlternateAppIcons).
// Deliberately thin: ask UIKit, report the answer, nothing else.
public class CmAppIconModule: Module {
  public func definition() -> ModuleDefinition {
    Name("CmAppIcon")

    // "This iPhone offers alternate icons".
    AsyncFunction("supportsAlternateIcons") { (promise: Promise) in
      DispatchQueue.main.async {
        promise.resolve(UIApplication.shared.supportsAlternateIcons)
      }
    }

    // The active alternate's plist name, or null for the default icon.
    AsyncFunction("getIconName") { (promise: Promise) in
      DispatchQueue.main.async {
        promise.resolve(UIApplication.shared.alternateIconName)
      }
    }

    // Applies the alternate, or null for the default icon. Resolves when the icon
    // changes; rejects when the iPhone declines (unsupported, unknown name, ...).
    AsyncFunction("setIconName") { (name: String?, promise: Promise) in
      DispatchQueue.main.async {
        guard UIApplication.shared.supportsAlternateIcons else {
          promise.reject(CmAppIconUnsupportedException())
          return
        }
        if UIApplication.shared.alternateIconName == name {
          promise.resolve()
          return
        }
        UIApplication.shared.setAlternateIconName(name) { error in
          if let error = error as NSError? {
            promise.reject(CmAppIconChangeException(error.localizedDescription))
          } else {
            promise.resolve()
          }
        }
      }
    }
  }
}

final class CmAppIconUnsupportedException: Exception {
  override var reason: String {
    "This iPhone does not offer alternate app icons"
  }
}

final class CmAppIconChangeException: GenericException<String> {
  override var reason: String {
    "The icon did not change: \(param)"
  }
}
