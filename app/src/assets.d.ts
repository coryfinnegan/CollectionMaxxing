// Bundled image assets resolve to their numeric asset id; the Metro and jest sides already agree.
declare module '*.png' {
  const value: number;
  export default value;
}
