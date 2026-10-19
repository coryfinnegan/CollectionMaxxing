// Web: the table by default, with a toggle to the grid (APP-1's grid, unchanged).
import { useState } from 'react';
import { View } from 'react-native';
import { LibraryTable } from '@/features/library-table/LibraryTable.web';
import { loadViewMode, saveViewMode, type ViewMode } from '@/features/library-table/viewMode';
import { spacing, useColors } from '@/theme/tokens';
import { Chip } from '@/ui';
import { LibraryGrid, type LibraryGridProps } from './LibraryGrid';

export type LibraryViewProps = LibraryGridProps;

export function LibraryView({ title, fixedStatus }: LibraryViewProps) {
  const colors = useColors();
  const [mode, setMode] = useState<ViewMode>(() => loadViewMode());
  const choose = (next: ViewMode) => {
    setMode(next);
    saveViewMode(next);
  };
  return (
    <View style={{ flex: 1, backgroundColor: colors.ground }}>
      <View
        accessibilityRole="toolbar"
        accessibilityLabel="Library view"
        style={{ flexDirection: 'row', gap: spacing.sm, paddingHorizontal: spacing.lg, paddingTop: spacing.md, paddingBottom: spacing.sm, backgroundColor: colors.ground }}
      >
        <Chip label="Table" accessibilityLabel="Table view" selected={mode === 'table'} onPress={() => choose('table')} />
        <Chip label="Grid" accessibilityLabel="Grid view" selected={mode === 'grid'} onPress={() => choose('grid')} />
      </View>
      {mode === 'table' ? <LibraryTable title={title} fixedStatus={fixedStatus} /> : <LibraryGrid title={title} fixedStatus={fixedStatus} />}
    </View>
  );
}
