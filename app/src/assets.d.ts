// Bundled image assets resolve to their numeric asset id (the castle textures in
// assets/castle/ are required this way); the Metro and jest sides already agree.
declare module '*.png' {
  const value: number;
  export default value;
}
