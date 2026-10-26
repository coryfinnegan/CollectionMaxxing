// The layered vector dragon (#365): one fixed silhouette drawn once, in ten
// separately movable layers. Poses only pick visibility and shifts (poses.ts);
// nothing here knows which pose is showing.
import { Circle, Ellipse, G, Path, Rect } from 'react-native-svg';
import { fixed, mascot } from '@/theme/tokens';
import type { LayerShift, LidState, PoseConfig } from './poses';

function shiftToTransform(shift?: LayerShift): string | undefined {
  if (!shift) return undefined;
  const parts: string[] = [];
  if (shift.x || shift.y) parts.push(`translate(${shift.x ?? 0} ${shift.y ?? 0})`);
  if (shift.rotate) {
    parts.push(shift.cx !== undefined && shift.cy !== undefined ? `rotate(${shift.rotate} ${shift.cx} ${shift.cy})` : `rotate(${shift.rotate})`);
  }
  return parts.length > 0 ? parts.join(' ') : undefined;
}

/** A book lying flat: cover, page block, two text lines. */
function FlatBook({ x, y, width, height }: { x: number; y: number; width: number; height: number }) {
  return (
    <G>
      <Rect x={x} y={y} width={width} height={height} rx={4} fill={mascot.cover} stroke={mascot.ink} strokeWidth={2.5} />
      <Rect x={x + 6} y={y + 3} width={width - 12} height={height - 6} rx={2} fill={mascot.page} />
      <Path d={`M${x + 12} ${y + 7} H${x + width - 12}`} stroke={mascot.ink} strokeWidth={1.5} />
      <Path d={`M${x + 12} ${y + 12} H${x + width - 24}`} stroke={mascot.ink} strokeWidth={1.5} />
    </G>
  );
}

function HoardLayer() {
  return (
    <G testID="mascot-layer-hoard">
      <Ellipse cx={120} cy={220} rx={95} ry={7} fill={mascot.ink} opacity={0.18} />
      <FlatBook x={34} y={196} width={120} height={20} />
      <FlatBook x={48} y={176} width={112} height={20} />
      <FlatBook x={62} y={158} width={100} height={18} />
      <FlatBook x={158} y={190} width={50} height={16} />
      <FlatBook x={162} y={176} width={46} height={14} />
    </G>
  );
}

function TailLayer() {
  const curl = 'M96 168 C130 190 160 198 192 190';
  return (
    <G testID="mascot-layer-tail">
      <Path d={curl} fill="none" stroke={mascot.ink} strokeWidth={25} strokeLinecap="round" />
      <Path d={curl} fill="none" stroke={mascot.body} strokeWidth={19} strokeLinecap="round" />
      <Path d="M192 190 l15 -8 l-5 15 z" fill={mascot.spike} stroke={mascot.ink} strokeWidth={2} strokeLinejoin="round" />
    </G>
  );
}

function BodyLayer() {
  return (
    <G testID="mascot-layer-body">
      <Ellipse cx={120} cy={172} rx={52} ry={6} fill={mascot.ink} opacity={0.15} />
      <Path d="M88 95 l-17 4 l11 11 z" fill={mascot.spike} stroke={mascot.ink} strokeWidth={2} strokeLinejoin="round" />
      <Path d="M84 112 l-17 4 l11 11 z" fill={mascot.spike} stroke={mascot.ink} strokeWidth={2} strokeLinejoin="round" />
      <Path d="M84 129 l-17 4 l11 11 z" fill={mascot.spike} stroke={mascot.ink} strokeWidth={2} strokeLinejoin="round" />
      <Ellipse cx={118} cy={122} rx={36} ry={44} fill={mascot.body} stroke={mascot.ink} strokeWidth={2.5} />
      <Ellipse cx={134} cy={130} rx={21} ry={32} fill={mascot.belly} />
      <Ellipse cx={96} cy={162} rx={16} ry={10} fill={mascot.body} stroke={mascot.ink} strokeWidth={2.5} />
      <Ellipse cx={142} cy={162} rx={16} ry={10} fill={mascot.body} stroke={mascot.ink} strokeWidth={2.5} />
    </G>
  );
}

function WingLayer() {
  const edge = 'M96 116 C78 92 64 68 56 44';
  return (
    <G testID="mascot-layer-wing">
      <Path
        d="M96 116 C70 90 58 66 56 44 C70 52 78 54 86 52 C84 66 88 72 96 76 C96 90 100 100 108 108 Z"
        fill={mascot.belly}
        stroke={mascot.ink}
        strokeWidth={2.5}
        strokeLinejoin="round"
      />
      <Path d={edge} fill="none" stroke={mascot.ink} strokeWidth={12} strokeLinecap="round" />
      <Path d={edge} fill="none" stroke={mascot.body} strokeWidth={8} strokeLinecap="round" />
    </G>
  );
}

function Lid({ state }: { state: LidState }) {
  if (state === 'open') {
    return (
      <G testID="mascot-lid-open">
        <Path d="M146 42 Q156 38 166 42" fill="none" stroke={mascot.ink} strokeWidth={2.5} strokeLinecap="round" />
      </G>
    );
  }
  if (state === 'squint') {
    return (
      <G testID="mascot-lid-squint">
        <Rect x={146} y={42} width={20} height={12} rx={6} fill={mascot.body} stroke={mascot.ink} strokeWidth={2} />
        <Path d="M147 54 H165" stroke={mascot.ink} strokeWidth={2.5} strokeLinecap="round" />
      </G>
    );
  }
  return (
    <G testID="mascot-lid-closed">
      <Ellipse cx={156} cy={54} rx={10} ry={12} fill={mascot.body} stroke={mascot.ink} strokeWidth={2} />
      <Path d="M147 54 Q156 59 165 54" fill="none" stroke={mascot.ink} strokeWidth={2.5} strokeLinecap="round" />
      <Path d="M147 54 l-4 -3" stroke={mascot.ink} strokeWidth={2} strokeLinecap="round" />
      <Path d="M165 54 l4 -3" stroke={mascot.ink} strokeWidth={2} strokeLinecap="round" />
    </G>
  );
}

function HeadLayer({ lid }: { lid: LidState }) {
  return (
    <G testID="mascot-layer-head">
      <Circle cx={148} cy={64} r={30} fill={mascot.body} stroke={mascot.ink} strokeWidth={2.5} />
      <Ellipse cx={180} cy={74} rx={24} ry={17} fill={mascot.body} stroke={mascot.ink} strokeWidth={2.5} />
      <Path d="M176 84 Q188 90 198 84" fill="none" stroke={mascot.ink} strokeWidth={2.5} strokeLinecap="round" />
      <Circle cx={196} cy={70} r={2} fill={mascot.ink} />
      {lid === 'closed' ? null : (
        <G>
          <Ellipse cx={156} cy={54} rx={9} ry={11} fill={fixed.white} stroke={mascot.ink} strokeWidth={2} />
          <Circle cx={158} cy={56} r={4.5} fill={mascot.pupil} />
          <Circle cx={159.5} cy={54.5} r={1.5} fill={fixed.white} />
        </G>
      )}
      <Lid state={lid} />
    </G>
  );
}

function HornsLayer() {
  return (
    <G testID="mascot-layer-horns">
      <Path d="M124 50 l-12 2 l9 9 z" fill={mascot.spike} stroke={mascot.ink} strokeWidth={2} strokeLinejoin="round" />
      <Path
        d="M132 40 C124 28 122 18 126 8 C132 14 138 16 142 16 C140 24 142 32 146 38 Z"
        fill={mascot.spike}
        stroke={mascot.ink}
        strokeWidth={2.5}
        strokeLinejoin="round"
      />
      <Path
        d="M152 34 C150 24 152 14 158 6 C162 13 166 16 170 17 C166 23 165 30 167 36 Z"
        fill={mascot.spike}
        stroke={mascot.ink}
        strokeWidth={2.5}
        strokeLinejoin="round"
      />
    </G>
  );
}

function BookLayer() {
  return (
    <G testID="mascot-layer-book">
      <G transform="rotate(10 179 133)">
        <Rect x={156} y={104} width={46} height={58} rx={5} fill={mascot.cover} stroke={mascot.ink} strokeWidth={2.5} />
        <Rect x={162} y={109} width={34} height={48} rx={3} fill={mascot.page} />
        <Path d="M168 120 H190" stroke={mascot.ink} strokeWidth={1.5} />
        <Path d="M168 128 H190" stroke={mascot.ink} strokeWidth={1.5} />
        <Path d="M168 136 H184" stroke={mascot.ink} strokeWidth={1.5} />
      </G>
      <Ellipse cx={198} cy={132} rx={10} ry={12} fill={mascot.body} stroke={mascot.ink} strokeWidth={2.5} />
      <Path d="M194 128 l6 2 M194 134 l6 2" stroke={mascot.ink} strokeWidth={1.5} strokeLinecap="round" />
    </G>
  );
}

function PageLayer() {
  return (
    <G testID="mascot-layer-page">
      <G transform="rotate(18 203 79)">
        <Rect x={192} y={64} width={22} height={30} rx={2} fill={fixed.white} stroke={mascot.ink} strokeWidth={2} />
        <Path d="M196 72 H210" stroke={mascot.ink} strokeWidth={1.5} />
        <Path d="M196 78 H210" stroke={mascot.ink} strokeWidth={1.5} />
      </G>
    </G>
  );
}

/** The milestone shield, shared by the milestone pose and the standalone SeriesShield: one drawing, two frames. */
export function ShieldLayer() {
  return (
    <G testID="mascot-layer-shield">
      <Path
        d="M157 108 H201 V134 C201 152 190 162 179 166 C168 162 157 152 157 134 Z"
        fill={mascot.cover}
        stroke={mascot.ink}
        strokeWidth={2.5}
        strokeLinejoin="round"
      />
      <Rect x={161} y={112} width={36} height={9} rx={2} fill={mascot.page} />
      <Circle cx={179} cy={140} r={9} fill={mascot.belly} stroke={mascot.ink} strokeWidth={2.5} />
    </G>
  );
}

const puffSpots: { cx: number; cy: number; r: number }[] = [
  { cx: 210, cy: 62, r: 7 },
  { cx: 222, cy: 54, r: 5 },
  { cx: 231, cy: 45, r: 3.5 },
];

function PuffsLayer({ count }: { count: 0 | 1 | 2 | 3 }) {
  if (count === 0) return null;
  return (
    <G testID="mascot-layer-puffs">
      {puffSpots.slice(0, count).map((spot, i) => (
        <Circle key={i} testID={`mascot-puff-${i}`} cx={spot.cx} cy={spot.cy} r={spot.r} fill={mascot.smoke} stroke={mascot.ink} strokeWidth={2} />
      ))}
    </G>
  );
}

/** Every visible layer in paint order. Outer groups carry the pose shifts, so the inner testID groups stay identical across poses. */
export function DragonLayers({ config }: { config: PoseConfig }) {
  const { layers, lid, puffs, shifts } = config;
  return (
    <G>
      {layers.hoard ? <HoardLayer /> : null}
      <G transform={shiftToTransform(shifts.dragon)}>
        {layers.wing ? (
          <G transform={shiftToTransform(shifts.wing)}>
            <WingLayer />
          </G>
        ) : null}
        {layers.body ? <BodyLayer /> : null}
        {layers.tail ? <TailLayer /> : null}
        {layers.head ? (
          <G transform={shiftToTransform(shifts.head)}>
            <HeadLayer lid={lid} />
            {layers.horns ? (
              <G transform={shiftToTransform(shifts.horns)}>
                <HornsLayer />
              </G>
            ) : null}
            <PuffsLayer count={puffs} />
          </G>
        ) : null}
        {layers.book ? (
          <G transform={shiftToTransform(shifts.book)}>
            <BookLayer />
          </G>
        ) : null}
        {layers.page ? (
          <G transform={shiftToTransform(shifts.page)}>
            <PageLayer />
          </G>
        ) : null}
        {layers.shield ? (
          <G transform={shiftToTransform(shifts.shield)}>
            <ShieldLayer />
          </G>
        ) : null}
      </G>
    </G>
  );
}
