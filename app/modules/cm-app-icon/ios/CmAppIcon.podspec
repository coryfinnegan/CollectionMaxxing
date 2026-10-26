Pod::Spec.new do |s|
  s.name           = 'CmAppIcon'
  s.version        = '1.0.0'
  s.summary        = 'Alternate home screen icons for CollectionMaxxing.'
  s.description    = 'A thin Expo module that switches between the registered app icons.'
  s.author         = ''
  s.homepage       = 'https://github.com/coryfinnegan/CollectionMaxxing'
  s.platforms      = { :ios => '16.0' }
  s.swift_version  = '5.9'
  s.source         = { git: '' }
  s.static_framework = true

  s.dependency 'ExpoModulesCore'

  s.pod_target_xcconfig = {
    'DEFINES_MODULE' => 'YES',
    'SWIFT_COMPILATION_MODE' => 'wholemodule'
  }

  s.source_files = "**/*.{h,m,mm,swift,hpp,cpp}"
end
