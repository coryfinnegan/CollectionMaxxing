Pod::Spec.new do |s|
  s.name           = 'CmVision'
  s.version        = '1.0.0'
  s.summary        = 'Apple Vision for CollectionMaxxing: text, rectangles, barcodes in stills, feature prints.'
  s.description    = 'A thin Expo module that calls Apple Vision and shapes the results.'
  s.author         = ''
  s.homepage       = 'https://github.com/coryfinnegan/CollectionMaxxing'
  s.platforms      = { :ios => '16.0' }
  s.source         = { git: '' }
  s.static_framework = true

  s.dependency 'ExpoModulesCore'

  s.pod_target_xcconfig = {
    'DEFINES_MODULE' => 'YES',
    'SWIFT_COMPILATION_MODE' => 'wholemodule'
  }

  s.source_files = "**/*.{h,m,mm,swift,hpp,cpp}"
end
