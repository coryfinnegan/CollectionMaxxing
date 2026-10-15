import SwiftUI
import PhotosUI
import Vision
import ImageIO
import FoundationModels

@Generable
struct ScanItem {
    @Guide(description: "The title of the item as printed")
    var title: String
    @Guide(description: "Kind of item, e.g. book, game, movie, music, card")
    var kind: String
    @Guide(description: "Platform if it is a video game, otherwise omit")
    var platform: String?
    @Guide(description: "Any text visible on the item")
    var visibleText: String
}

@Generable
struct ScanResult {
    @Guide(description: "Every distinct collectible item visible in the image")
    var items: [ScanItem]
}

struct CameraPicker: UIViewControllerRepresentable {
    var onImage: (UIImage) -> Void
    func makeUIViewController(context: Context) -> UIImagePickerController {
        let p = UIImagePickerController()
        p.sourceType = UIImagePickerController.isSourceTypeAvailable(.camera) ? .camera : .photoLibrary
        p.delegate = context.coordinator
        return p
    }
    func updateUIViewController(_ vc: UIImagePickerController, context: Context) {}
    func makeCoordinator() -> Coordinator { Coordinator(onImage) }
    final class Coordinator: NSObject, UIImagePickerControllerDelegate, UINavigationControllerDelegate {
        let onImage: (UIImage) -> Void
        init(_ f: @escaping (UIImage) -> Void) { onImage = f }
        func imagePickerController(_ picker: UIImagePickerController, didFinishPickingMediaWithInfo info: [UIImagePickerController.InfoKey: Any]) {
            if let i = info[.originalImage] as? UIImage { onImage(i) }
            picker.dismiss(animated: true)
        }
        func imagePickerControllerDidCancel(_ picker: UIImagePickerController) { picker.dismiss(animated: true) }
    }
}

func cgOrientation(_ o: UIImage.Orientation) -> CGImagePropertyOrientation {
    switch o {
    case .up: return .up
    case .upMirrored: return .upMirrored
    case .down: return .down
    case .downMirrored: return .downMirrored
    case .left: return .left
    case .leftMirrored: return .leftMirrored
    case .right: return .right
    case .rightMirrored: return .rightMirrored
    @unknown default: return .up
    }
}

func secs(_ d: Date) -> String { String(format: "%.2fs", Date().timeIntervalSince(d)) }

struct ContentView: View {
    @State private var pick: PhotosPickerItem?
    @State private var image: UIImage?
    @State private var showCamera = false
    @State private var status = ""
    @State private var modelOut = "(not run)"
    @State private var barcodeOut = "(not run)"
    @State private var textOut = "(not run)"

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 12) {
                Text("Model status").font(.headline)
                Text(status).font(.footnote.monospaced())
                HStack {
                    PhotosPicker("Pick photo", selection: $pick, matching: .images)
                    Button("Take photo") { showCamera = true }
                }.buttonStyle(.bordered)
                if let image { Image(uiImage: image).resizable().scaledToFit().frame(maxHeight: 200) }
                Button("Read with the model") { Task { await runModel() } }.buttonStyle(.borderedProminent)
                Text(modelOut).font(.footnote.monospaced())
                Button("Vision barcodes") { Task { await runBarcodes() } }.buttonStyle(.bordered)
                Text(barcodeOut).font(.footnote.monospaced())
                Button("Vision text") { Task { await runText() } }.buttonStyle(.bordered)
                Text(textOut).font(.footnote.monospaced())
                Button("Copy report") { UIPasteboard.general.string = report() }.buttonStyle(.borderedProminent)
            }.padding()
        }
        .onAppear { status = modelStatus() }
        .onChange(of: pick) { _, item in
            Task {
                if let d = try? await item?.loadTransferable(type: Data.self), let i = UIImage(data: d) { image = i }
            }
        }
        .sheet(isPresented: $showCamera) { CameraPicker { image = $0 } }
    }

    func modelStatus() -> String {
        let m = SystemLanguageModel.default
        var s = "availability: \(m.availability)\n"
        s += "capabilities.vision: \(m.capabilities.contains(.vision))\n"
        s += "capabilities.guidedGeneration: \(m.capabilities.contains(.guidedGeneration))\n"
        s += "supportsLocale: \(m.supportsLocale())"
        return s
    }

    func runModel() async {
        guard let ui = image, let cg = ui.cgImage else { modelOut = "No image"; return }
        let o = cgOrientation(ui.imageOrientation)
        let start = Date()
        do {
            let session = LanguageModelSession(instructions: "You identify collectible items (books, games, movies, music, cards) in photos.")
            let stream = session.streamResponse(generating: ScanResult.self) {
                "List every collectible item in this photo."
                Attachment(cg, orientation: o)
            }
            var last = ""
            for try await partial in stream {
                last = "\(partial.content)"
                modelOut = "streaming (\(secs(start)))...\n\(last)"
            }
            modelOut = "done in \(secs(start))\n\(last)"
        } catch {
            modelOut = "ERROR after \(secs(start))\n\(type(of: error)): \(error)\n\(String(reflecting: error))"
        }
    }

    func runBarcodes() async {
        guard let ui = image, let cg = ui.cgImage else { barcodeOut = "No image"; return }
        let o = cgOrientation(ui.imageOrientation)
        let start = Date()
        let result: String = await Task.detached {
            do {
                let req = VNDetectBarcodesRequest()
                try VNImageRequestHandler(cgImage: cg, orientation: o).perform([req])
                let r = (req.results ?? []).map { "\($0.symbology.rawValue): \($0.payloadStringValue ?? "nil")" }
                return r.isEmpty ? "none found" : r.joined(separator: "\n")
            } catch { return "ERROR: \(error)" }
        }.value
        barcodeOut = "\(secs(start))\n\(result)"
    }

    func runText() async {
        guard let ui = image, let cg = ui.cgImage else { textOut = "No image"; return }
        let o = cgOrientation(ui.imageOrientation)
        let start = Date()
        let result: String = await Task.detached {
            do {
                let req = VNRecognizeTextRequest()
                req.recognitionLevel = .accurate
                try VNImageRequestHandler(cgImage: cg, orientation: o).perform([req])
                let r = (req.results ?? []).compactMap { $0.topCandidates(1).first?.string }
                return r.isEmpty ? "none found" : r.joined(separator: "\n")
            } catch { return "ERROR: \(error)" }
        }.value
        textOut = "\(secs(start))\n\(result)"
    }

    func report() -> String {
        """
        CMSpike report
        device: \(UIDevice.current.model) \(UIDevice.current.systemName) \(UIDevice.current.systemVersion)
        \(modelStatus())

        MODEL:
        \(modelOut)

        BARCODES:
        \(barcodeOut)

        TEXT:
        \(textOut)
        """
    }
}
