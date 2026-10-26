// The Mall room on the web (#357): the collection as real-time 3D rooms. Each
// room builds a procedural shell in its Blender palette, stands the covers at
// the GLB anchor transforms (procedural anchors when the room file cannot be
// read), and offers an orbit camera, the Blender camera presets, and
// tap-to-identify through the anchors. The aisle list under the room carries
// the same items for keyboards, VoiceOver, and large text: the canvas itself
// stays out of the accessibility tree.
import { useEffect, useMemo, useRef, useState } from 'react';
import { View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { Asset } from 'expo-asset';
import * as THREE from 'three';
import videoGlb from '@/assets/stores/video_store.glb';
import toyGlb from '@/assets/stores/toy_store.glb';
import bookstoreGlb from '@/assets/stores/bookstore.glb';
import mallHubGlb from '@/assets/stores/mall_hub.glb';
import { fixed, mallPalette, type MallRoomPalette } from '@/theme/tokens';
import { LoadingState, Text } from '@/ui';
import { roomLabel, type AnchorSlice } from './anchors';
import { blenderToThree, extractGlbJson, lensToFov, parseGlbAnchors, type GlbAnchor } from './glb';
import { roomAnchors, roomDims, roomPresets, type MallRoomId } from './rooms';
import type { RoomCanvasProps } from './RoomCanvas';

const paletteFor: Record<MallRoomId, MallRoomPalette> = {
  video_store: 'video',
  toy_store: 'toy',
  bookstore: 'book',
  mall_hub: 'hub',
};

const roomAsset: Record<MallRoomId, number> = {
  video_store: videoGlb,
  toy_store: toyGlb,
  bookstore: bookstoreGlb,
  mall_hub: mallHubGlb,
};

const COVER_W = 0.32;
const COVER_H = 0.48;
const COVER_GAP = 0.05;
const ANCHOR_TIMEOUT_MS = 4000;

/** The GLB anchor transforms, or null when the room file cannot be read. */
async function loadAnchors(room: MallRoomId): Promise<GlbAnchor[] | null> {
  try {
    const asset = Asset.fromModule(roomAsset[room]);
    await asset.downloadAsync();
    const uri = asset.localUri ?? asset.uri;
    if (!uri) return null;
    const response = await fetch(uri);
    if (!response.ok) return null;
    const anchors = parseGlbAnchors(extractGlbJson(await response.arrayBuffer()));
    return anchors.length > 0 ? anchors : null;
  } catch {
    return null;
  }
}

type AnchorPose = { position: [number, number, number]; yaw: number };

/** The GLB poses by anchor name, with procedural stand-ins for any gaps. */
function posesFor(room: MallRoomId, anchors: GlbAnchor[]): Map<string, AnchorPose> {
  const dims = roomDims[room];
  const names = roomAnchors[room];
  const poses = new Map<string, AnchorPose>();
  for (const anchor of anchors) poses.set(anchor.name, { position: anchor.position, yaw: anchor.yaw });
  names.forEach((name, order) => {
    if (poses.has(name)) return;
    const spread = names.length === 1 ? 0 : (order / (names.length - 1) - 0.5) * dims.width * 0.7;
    poses.set(name, { position: [spread, 1.4, -dims.depth / 2 + 0.9], yaw: 0 });
  });
  return poses;
}

function spineFor(index: number): string {
  return mallPalette.spines[index % mallPalette.spines.length];
}

/** A generated cover tile: spine band plus the title, from the token palette. */
function coverTexture(title: string, spine: string): THREE.CanvasTexture {
  const canvas = document.createElement('canvas');
  canvas.width = 128;
  canvas.height = 192;
  const paint = canvas.getContext('2d');
  if (paint) {
    paint.fillStyle = fixed.white;
    paint.fillRect(0, 0, canvas.width, canvas.height);
    paint.fillStyle = spine;
    paint.fillRect(0, 0, 22, canvas.height);
    paint.fillStyle = fixed.black;
    paint.font = '600 15px sans-serif';
    const lines: string[] = [];
    let line = '';
    for (const word of title.split(' ')) {
      const next = line ? `${line} ${word}` : word;
      if (line && paint.measureText(next).width > 88) {
        lines.push(line);
        line = word;
      } else {
        line = next;
      }
    }
    if (line) lines.push(line);
    lines.slice(0, 7).forEach((row, index) => paint.fillText(row, 30, 30 + index * 19));
  }
  const texture = new THREE.CanvasTexture(canvas);
  texture.colorSpace = THREE.SRGBColorSpace;
  return texture;
}

type Flight = { fromPos: THREE.Vector3; toPos: THREE.Vector3; fromTarget: THREE.Vector3; toTarget: THREE.Vector3; start: number };

type RoomScene = {
  dispose: () => void;
  flyTo: (position: [number, number, number], target: [number, number, number], fov: number, instant: boolean) => void;
};

function buildRoomScene(
  host: HTMLDivElement,
  room: MallRoomId,
  slices: AnchorSlice[],
  poses: Map<string, AnchorPose>,
  onIdentify: (id: string) => void,
): RoomScene {
  const palette = mallPalette[paletteFor[room]];
  const dims = roomDims[room];
  const renderer = new THREE.WebGLRenderer({ antialias: true });
  renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
  renderer.shadowMap.enabled = true;
  renderer.shadowMap.type = THREE.PCFSoftShadowMap;
  renderer.domElement.style.display = 'block';
  renderer.domElement.style.touchAction = 'none';
  renderer.domElement.style.cursor = 'grab';
  host.appendChild(renderer.domElement);

  const scene = new THREE.Scene();
  scene.background = new THREE.Color(palette.wall);
  const camera = new THREE.PerspectiveCamera(43, 1, 0.1, 100);

  scene.add(new THREE.HemisphereLight(new THREE.Color(fixed.white), new THREE.Color(palette.floor), 0.85));
  const sun = new THREE.DirectionalLight(new THREE.Color(fixed.white), 1.1);
  sun.position.set(4, 7, 3);
  sun.castShadow = true;
  sun.shadow.mapSize.set(1024, 1024);
  sun.shadow.camera.left = -12;
  sun.shadow.camera.right = 12;
  sun.shadow.camera.top = 12;
  sun.shadow.camera.bottom = -12;
  scene.add(sun);

  const standard = (color: string, roughness = 0.9) => new THREE.MeshStandardMaterial({ color: new THREE.Color(color), roughness });
  const floor = new THREE.Mesh(new THREE.PlaneGeometry(dims.width, dims.depth), standard(palette.floor));
  floor.rotation.x = -Math.PI / 2;
  floor.receiveShadow = true;
  scene.add(floor);
  const back = new THREE.Mesh(new THREE.PlaneGeometry(dims.width, dims.height), standard(palette.wall));
  back.position.set(0, dims.height / 2, -dims.depth / 2);
  scene.add(back);
  const left = new THREE.Mesh(new THREE.PlaneGeometry(dims.depth, dims.height), standard(palette.wall));
  left.rotation.y = Math.PI / 2;
  left.position.set(-dims.width / 2, dims.height / 2, 0);
  scene.add(left);
  const right = left.clone();
  right.rotation.y = -Math.PI / 2;
  right.position.x = dims.width / 2;
  scene.add(right);
  const fixture = new THREE.Mesh(new THREE.BoxGeometry(dims.width * 0.8, 1.1, 0.4), standard(palette.fixture, 0.8));
  fixture.position.set(0, 0.55, -dims.depth / 2 + 0.45);
  fixture.castShadow = true;
  fixture.receiveShadow = true;
  scene.add(fixture);
  const trim = new THREE.Mesh(new THREE.BoxGeometry(dims.width * 0.8, 0.06, 0.44), standard(palette.trim, 0.6));
  trim.position.set(0, 1.13, -dims.depth / 2 + 0.45);
  scene.add(trim);

  const covers: THREE.Mesh[] = [];
  const coverShape = new THREE.PlaneGeometry(COVER_W, COVER_H);
  slices.forEach((slice, sliceOrder) => {
    const pose = poses.get(slice.name);
    if (!pose || slice.items.length === 0) return;
    const [px, py, pz] = pose.position;
    const facing = new THREE.Vector3(Math.sin(pose.yaw), 0, Math.cos(pose.yaw));
    const across = new THREE.Vector3(facing.z, 0, -facing.x);
    const row = (slice.items.length - 1) * (COVER_W + COVER_GAP);
    slice.items.forEach((item, order) => {
      const texture = coverTexture(item.title, spineFor(sliceOrder + order));
      const cover = new THREE.Mesh(coverShape, new THREE.MeshStandardMaterial({ map: texture, roughness: 0.7 }));
      const along = order * (COVER_W + COVER_GAP) - row / 2;
      cover.position.set(px + across.x * along, py, pz + across.z * along);
      cover.rotation.y = pose.yaw;
      cover.castShadow = true;
      cover.userData.itemId = item.id;
      scene.add(cover);
      covers.push(cover);
    });
  });

  const orbit = { target: new THREE.Vector3(), yaw: 0, pitch: 0.3, dist: 8 };
  const placeCamera = () => {
    camera.position.set(
      orbit.target.x + orbit.dist * Math.cos(orbit.pitch) * Math.sin(orbit.yaw),
      orbit.target.y + orbit.dist * Math.sin(orbit.pitch),
      orbit.target.z + orbit.dist * Math.cos(orbit.pitch) * Math.cos(orbit.yaw),
    );
    camera.lookAt(orbit.target);
  };
  let flight: Flight | null = null;
  const flyTo: RoomScene['flyTo'] = (position, target, fov, instant) => {
    const toPos = new THREE.Vector3(...position);
    const toTarget = new THREE.Vector3(...target);
    camera.fov = fov;
    camera.updateProjectionMatrix();
    if (instant) {
      camera.position.copy(toPos);
      orbit.target.copy(toTarget);
      camera.lookAt(orbit.target);
      const offset = toPos.clone().sub(toTarget);
      orbit.dist = offset.length();
      orbit.yaw = Math.atan2(offset.x, offset.z);
      orbit.pitch = Math.asin(Math.max(-1, Math.min(1, offset.y / Math.max(orbit.dist, 0.001))));
      flight = null;
      return;
    }
    flight = { fromPos: camera.position.clone(), toPos, fromTarget: orbit.target.clone(), toTarget, start: performance.now() };
  };

  const pointers = new Map<number, { x: number; y: number }>();
  let down: { x: number; y: number } | null = null;
  let pinch = 0;
  const canvas = renderer.domElement;
  const onDown = (event: PointerEvent) => {
    pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
    if (pointers.size === 1) {
      down = { x: event.clientX, y: event.clientY };
      flight = null;
      canvas.style.cursor = 'grabbing';
      try {
        canvas.setPointerCapture(event.pointerId);
      } catch {
        // A capture failure still leaves drag working; the pointerup lands on the canvas.
      }
    } else if (pointers.size === 2) {
      const [a, b] = [...pointers.values()];
      pinch = Math.hypot(a.x - b.x, a.y - b.y);
    }
  };
  const onMove = (event: PointerEvent) => {
    const seen = pointers.get(event.pointerId);
    if (!seen) return;
    if (pointers.size === 2) {
      seen.x = event.clientX;
      seen.y = event.clientY;
      const [a, b] = [...pointers.values()];
      const span = Math.hypot(a.x - b.x, a.y - b.y);
      if (pinch > 0) orbit.dist = Math.max(2, Math.min(20, (orbit.dist * pinch) / Math.max(span, 1)));
      pinch = span;
      return;
    }
    const dx = event.clientX - seen.x;
    const dy = event.clientY - seen.y;
    seen.x = event.clientX;
    seen.y = event.clientY;
    orbit.yaw -= dx * 0.005;
    orbit.pitch = Math.max(-0.1, Math.min(1.2, orbit.pitch + dy * 0.004));
  };
  const raycaster = new THREE.Raycaster();
  const pointer = new THREE.Vector2();
  const onUp = (event: PointerEvent) => {
    pointers.delete(event.pointerId);
    canvas.style.cursor = 'grab';
    if (down && pointers.size === 0 && Math.hypot(event.clientX - down.x, event.clientY - down.y) < 6) {
      const box = canvas.getBoundingClientRect();
      pointer.set(((event.clientX - box.left) / box.width) * 2 - 1, -((event.clientY - box.top) / box.height) * 2 + 1);
      raycaster.setFromCamera(pointer, camera);
      const hit = raycaster.intersectObjects(covers, false)[0];
      const itemId = hit?.object.userData.itemId;
      if (typeof itemId === 'string') onIdentify(itemId);
    }
    if (pointers.size === 0) down = null;
  };
  const onWheel = (event: WheelEvent) => {
    event.preventDefault();
    orbit.dist = Math.max(2, Math.min(20, orbit.dist * (1 + event.deltaY * 0.001)));
  };
  canvas.addEventListener('pointerdown', onDown);
  canvas.addEventListener('pointermove', onMove);
  canvas.addEventListener('pointerup', onUp);
  canvas.addEventListener('pointercancel', onUp);
  canvas.addEventListener('wheel', onWheel, { passive: false });

  const fit = () => {
    const width = Math.max(host.clientWidth, 1);
    const height = Math.max(host.clientHeight, 1);
    renderer.setSize(width, height, false);
    camera.aspect = width / height;
    camera.updateProjectionMatrix();
  };
  fit();
  const observer = new ResizeObserver(fit);
  observer.observe(host);

  let frame = 0;
  const tick = (now: number) => {
    frame = requestAnimationFrame(tick);
    if (flight) {
      const t = Math.min((now - flight.start) / 450, 1);
      const eased = t * t * (3 - 2 * t);
      camera.position.lerpVectors(flight.fromPos, flight.toPos, eased);
      orbit.target.lerpVectors(flight.fromTarget, flight.toTarget, eased);
      camera.lookAt(orbit.target);
      if (t >= 1) {
        const offset = camera.position.clone().sub(orbit.target);
        orbit.dist = offset.length();
        orbit.yaw = Math.atan2(offset.x, offset.z);
        orbit.pitch = Math.asin(Math.max(-1, Math.min(1, offset.y / Math.max(orbit.dist, 0.001))));
        flight = null;
      }
    } else {
      placeCamera();
    }
    renderer.render(scene, camera);
  };
  frame = requestAnimationFrame(tick);

  return {
    flyTo,
    dispose: () => {
      cancelAnimationFrame(frame);
      observer.disconnect();
      canvas.removeEventListener('pointerdown', onDown);
      canvas.removeEventListener('pointermove', onMove);
      canvas.removeEventListener('pointerup', onUp);
      canvas.removeEventListener('pointercancel', onUp);
      canvas.removeEventListener('wheel', onWheel);
      scene.traverse((node) => {
        const mesh = node as THREE.Mesh;
        if (mesh.isMesh) {
          mesh.geometry.dispose();
          const materials = Array.isArray(mesh.material) ? mesh.material : [mesh.material];
          for (const material of materials) {
            const mapped = (material as THREE.MeshStandardMaterial).map;
            if (mapped) mapped.dispose();
            material.dispose();
          }
        }
      });
      coverShape.dispose();
      renderer.dispose();
      canvas.remove();
    },
  };
}

export function RoomCanvas({ room, presetIndex, slices, selectedId, onIdentify, reduceMotion }: RoomCanvasProps) {
  // The identify card below names the selection; in-scene highlighting is a follow-up (#357).
  void selectedId;
  const palette = mallPalette[paletteFor[room]];
  const hostRef = useRef<HTMLDivElement | null>(null);
  const sceneRef = useRef<RoomScene | null>(null);
  const [loaded, setLoaded] = useState<{ room: MallRoomId; anchors: GlbAnchor[] } | null>(null);
  const presets = roomPresets[room];
  const preset = presets[Math.min(presetIndex, presets.length - 1)];

  useEffect(() => {
    let alive = true;
    const timeout = new Promise<null>((resolve) => {
      setTimeout(() => resolve(null), ANCHOR_TIMEOUT_MS);
    });
    void Promise.race([loadAnchors(room), timeout]).then((found) => {
      if (alive) setLoaded({ room, anchors: found ?? [] });
    });
    return () => {
      alive = false;
    };
  }, [room]);

  // Anchors for any other room are stale: the room rebuilds from its own file.
  const anchors = loaded?.room === room ? loaded.anchors : null;

  const slicesSig = useMemo(
    () => slices.map((slice) => `${slice.name}:${slice.items.map((item) => item.id).join(',')}`).join(';'),
    [slices],
  );
  const anchorsSig = useMemo(
    () => (anchors ?? []).map((anchor) => `${anchor.name}:${anchor.position.map((n) => n.toFixed(2)).join(',')}`).join(';'),
    [anchors],
  );

  useEffect(() => {
    const host = hostRef.current;
    if (!host || anchors === null) return;
    const scene = buildRoomScene(host, room, slices, posesFor(room, anchors), onIdentify);
    sceneRef.current = scene;
    const hero = presets[0];
    scene.flyTo(blenderToThree(hero.location), blenderToThree(hero.target), lensToFov(hero.lens), true);
    return () => {
      sceneRef.current = null;
      scene.dispose();
    };
    // The signatures stand in for the deep values; the room rebuilds only when they change.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [room, slicesSig, anchorsSig]);

  useEffect(() => {
    sceneRef.current?.flyTo(blenderToThree(preset.location), blenderToThree(preset.target), lensToFov(preset.lens), reduceMotion);
  }, [preset, reduceMotion]);

  return (
    <View style={styles.room(palette.wall)} testID="mall-room">
      <Text variant="heading" style={styles.sign(palette.sign)}>
        {roomLabel(room)}
      </Text>
      {anchors === null ? (
        <View style={styles.loading}>
          <LoadingState label="Loading the room…" />
        </View>
      ) : null}
      <View
        ref={(node) => {
          hostRef.current = node as unknown as HTMLDivElement | null;
        }}
        accessibilityElementsHidden
        importantForAccessibility="no-hide-descendants"
        style={styles.canvas(anchors === null)}
      />
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  room: (wall: string) => ({ backgroundColor: wall, borderRadius: theme.radius.lg, overflow: 'hidden', gap: theme.spacing.md, paddingTop: theme.spacing.md }),
  sign: (color: string) => ({ color, textAlign: 'center' }),
  loading: { height: 320, justifyContent: 'center' },
  canvas: (hidden: boolean) => ({ height: 320, width: '100%', display: hidden ? 'none' : 'flex' }),
}));
