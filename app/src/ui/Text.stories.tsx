import type { Meta, StoryObj } from '@storybook/react';
import { Text } from './Text';

const meta: Meta<typeof Text> = {
  title: 'Primitives/Text',
  component: Text,
  args: { children: 'The Matrix' },
};

export default meta;
type Story = StoryObj<typeof Text>;

export const Display: Story = { args: { variant: 'display' } };
export const Title: Story = { args: { variant: 'title' } };
export const Heading: Story = { args: { variant: 'heading' } };
export const Body: Story = { args: { variant: 'body' } };
export const Label: Story = { args: { variant: 'label' } };
export const Caption: Story = { args: { variant: 'caption' } };
export const Mono: Story = { args: { variant: 'mono', children: 'DVD · 1999' } };
