// Acquired day on the item edit form (#441): the platform date picker on native, a
// typed YYYY-MM-DD field on the web (the picker package renders nothing there).
// First: this module needs Unistyles configured before it runs (#305).
import '@/theme/unistyles';
import DateTimePicker, { type DateTimePickerEvent } from '@react-native-community/datetimepicker';
import { useState } from 'react';
import { Platform, View } from 'react-native';
import { StyleSheet } from 'react-native-unistyles';
import { localDateString, parseLocalDate } from '@/lib/dates';
import { Button, Text, TextField } from '@/ui';

export function AcquiredField({ value, onChange, error }: { value: string; onChange: (date: string) => void; error?: string | null }) {
  const [open, setOpen] = useState(false);
  if (Platform.OS === 'web') {
    return <TextField label="Acquired" value={value} onChangeText={onChange} placeholder="2024-06-02" error={error} />;
  }
  const picked = (event: DateTimePickerEvent, date?: Date) => {
    if (event.type === 'dismissed') {
      setOpen(false);
      return;
    }
    if (date) onChange(localDateString(date));
    if (Platform.OS === 'android') setOpen(false);
  };
  return (
    <View style={styles.field}>
      <Text variant="label">Acquired</Text>
      <View style={styles.row}>
        <View style={styles.trigger}>
          <Button
            label={value || 'Not set'}
            accessibilityLabel="Acquired"
            accessibilityValue={{ text: value || 'Not set' }}
            variant="secondary"
            onPress={() => setOpen(!open)}
          />
        </View>
        {value ? <Button label="Clear date" variant="secondary" onPress={() => onChange('')} /> : null}
      </View>
      {open ? (
        <DateTimePicker value={parseLocalDate(value) ?? new Date()} mode="date" display={Platform.OS === 'ios' ? 'inline' : 'default'} onChange={picked} />
      ) : null}
    </View>
  );
}

const styles = StyleSheet.create((theme) => ({
  field: { gap: theme.spacing.xs },
  row: { flexDirection: 'row', gap: theme.spacing.sm, alignItems: 'center' },
  trigger: { flex: 1 },
}));
