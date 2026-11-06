import Cocoa
import FlutterMacOS

class MainFlutterWindow: NSWindow {
  override func awakeFromNib() {
    let flutterViewController = FlutterViewController()
    let midnight = NSColor(
      calibratedRed: 7.0 / 255.0,
      green: 16.0 / 255.0,
      blue: 26.0 / 255.0,
      alpha: 1.0
    )
    let windowFrame = self.frame
    self.backgroundColor = midnight
    flutterViewController.view.wantsLayer = true
    flutterViewController.view.layer?.backgroundColor = midnight.cgColor
    self.contentViewController = flutterViewController
    self.setFrame(windowFrame, display: true)

    RegisterGeneratedPlugins(registry: flutterViewController)

    super.awakeFromNib()
  }
}
