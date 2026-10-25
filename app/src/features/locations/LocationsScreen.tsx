// Me → Locations: the reference slice. A tree list with add, rename and delete, all through the generated hooks.
import { useMemo, useState } from 'react';
import { View } from 'react-native';
import { friendlyError } from '@/lib/problem';
import { spacing } from '@/theme/tokens';
import { Button, Card, EmptyState, ListRow, LoadingState, Screen, Text, TextField } from '@/ui';
import { BackButton } from '@/features/me/BackButton';
import { flattenTree } from './tree';
import { isPendingLocation, useActiveLibraryId, useLocationMutations, useLocationsList } from './useLocations';

export function LocationsScreen() {
  const { libraryId, isLoading: loadingLibrary } = useActiveLibraryId();
  if (loadingLibrary || !libraryId) {
    return (
      <Screen title="Locations">
        <BackButton />
        <LoadingState label="Loading your locations…" />
      </Screen>
    );
  }
  return <LocationsList libraryId={libraryId} />;
}

function LocationsList({ libraryId }: { libraryId: string }) {
  const { locations, isLoading, isError, refetch } = useLocationsList(libraryId);
  const { create, rename, remove } = useLocationMutations(libraryId);
  const rows = useMemo(() => flattenTree(locations), [locations]);

  const [newName, setNewName] = useState('');
  const [parentId, setParentId] = useState<string | null>(null);
  const [addError, setAddError] = useState<string | null>(null);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [editName, setEditName] = useState('');
  const [rowError, setRowError] = useState<string | null>(null);

  const parent = locations.find((l) => l.id === parentId);

  const add = () => {
    setAddError(null);
    create.mutate(
      { libraryId, data: { name: newName, parentId } },
      {
        onSuccess: () => {
          setNewName('');
          setParentId(null);
        },
        onError: (error) => setAddError(friendlyError(error)),
      },
    );
  };

  const saveRename = (id: string) => {
    setRowError(null);
    rename.mutate(
      { libraryId, id, data: { name: editName } },
      {
        onSuccess: () => setEditingId(null),
        onError: (error) => setRowError(friendlyError(error)),
      },
    );
  };

  const deleteLocation = (id: string) => {
    setRowError(null);
    remove.mutate({ libraryId, id }, { onError: (error) => setRowError(friendlyError(error)) });
  };

  return (
    <Screen title="Locations" subtitle="Where things live: rooms, shelves, boxes.">
      <BackButton />
      <Card>
        {parent ? (
          <View style={{ flexDirection: 'row', alignItems: 'center', gap: spacing.sm }}>
            <Text variant="label" style={{ flex: 1 }}>{`Adding inside ${parent.name}`}</Text>
            <Button label="Cancel" variant="secondary" onPress={() => setParentId(null)} />
          </View>
        ) : null}
        <TextField
          label="New location"
          value={newName}
          onChangeText={setNewName}
          onSubmitEditing={add}
          placeholder="Shelf 3"
          error={addError}
        />
        <Button label="Add location" onPress={add} busy={create.isPending} />
      </Card>

      {rowError ? (
        <Text tone="danger" accessibilityRole="alert">
          {rowError}
        </Text>
      ) : null}

      {isError ? (
        <EmptyState
          title="Couldn't load your locations"
          body="Check your connection and try again."
          actionLabel="Try again"
          onAction={() => refetch()}
        />
      ) : isLoading ? (
        <LoadingState label="Loading your locations…" />
      ) : rows.length === 0 ? (
        <EmptyState title="No locations yet" body="Add a room or a shelf to say where things live." />
      ) : (
        <View>
          {rows.map(({ location, depth }) => {
            // The row is already visible from the optimistic update, but until the server
            // answers there is nothing real to add inside, rename or delete.
            const pending = isPendingLocation(location.id);
            return editingId === location.id ? (
              <View key={location.id} style={{ gap: spacing.sm, paddingVertical: spacing.sm, paddingLeft: depth * spacing.xl }}>
                <TextField label={`Rename ${location.name}`} value={editName} onChangeText={setEditName} autoFocus />
                <View style={{ flexDirection: 'row', gap: spacing.sm }}>
                  <Button label="Save name" onPress={() => saveRename(location.id)} busy={rename.isPending} />
                  <Button label="Cancel" variant="secondary" onPress={() => setEditingId(null)} />
                </View>
              </View>
            ) : (
              <ListRow
                key={location.id}
                testID={`location-${location.id}`}
                title={location.name}
                depth={depth}
                trailing={
                  <View style={{ flexDirection: 'row', gap: spacing.sm }}>
                    <Button
                      label="Add inside"
                      accessibilityLabel={`Add inside ${location.name}`}
                      variant="secondary"
                      disabled={pending}
                      onPress={() => setParentId(location.id)}
                    />
                    <Button
                      label="Rename"
                      accessibilityLabel={`Rename ${location.name}`}
                      variant="secondary"
                      disabled={pending}
                      onPress={() => {
                        setEditingId(location.id);
                        setEditName(location.name);
                      }}
                    />
                    <Button
                      label="Delete"
                      accessibilityLabel={`Delete ${location.name}`}
                      variant="danger"
                      disabled={pending}
                      onPress={() => deleteLocation(location.id)}
                    />
                  </View>
                }
              />
            );
          })}
        </View>
      )}
    </Screen>
  );
}
