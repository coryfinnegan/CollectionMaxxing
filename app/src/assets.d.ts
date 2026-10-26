// Bundled image assets resolve to their numeric asset id (the castle textures in
// assets/castle/ are required this way); the Metro and jest sides already agree.
declare module '*.png' {
  const value: number;
  export default value;
}

// Mall store rooms (#381) resolve the same way once the Mall prototype teaches
// Metro the .glb extension; nothing requires them yet.
declare module '*.glb' {
  const value: number;
  export default value;
}
