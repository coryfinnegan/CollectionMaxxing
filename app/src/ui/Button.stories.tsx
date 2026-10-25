import type { Meta, StoryObj } from '@storybook/react';
import { fn } from 'storybook/test';
import { Button } from './Button';

const meta: Meta<typeof Button> = {
  title: 'Primitives/Button',
  component: Button,
  args: { onPress: fn() },
};

export default meta;
type Story = StoryObj<typeof Button>;

export const Primary: Story = { args: { label: 'Add 11 items' } };
export const Secondary: Story = { args: { label: 'Not now', variant: 'secondary' } };
export const Danger: Story = { args: { label: 'Delete', variant: 'danger' } };
export const Busy: Story = { args: { label: 'Add 11 items', busy: true } };
