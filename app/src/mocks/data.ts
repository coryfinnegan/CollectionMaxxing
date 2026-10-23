// In-memory data behind the MSW handlers, used in mock mode and in tests.
import type { LocationDto, MeResponse } from '@/api/generated/models';

export const MOCK_LIBRARY_ID = '11111111-1111-4111-8111-111111111111';

export const mockMe = (): MeResponse => ({
  id: '22222222-2222-4222-8222-222222222222',
  memberships: [{ libraryId: MOCK_LIBRARY_ID, role: 'owner' }],
  displayName: 'Cory',
  email: 'cory@example.com',
});

const seed = (): LocationDto[] => [
  { id: 'a0000000-0000-4000-8000-000000000001', parentId: null, name: 'Game room', sortOrder: 0 },
  { id: 'a0000000-0000-4000-8000-000000000002', parentId: 'a0000000-0000-4000-8000-000000000001', name: 'Shelf 1', sortOrder: 0 },
  { id: 'a0000000-0000-4000-8000-000000000003', parentId: 'a0000000-0000-4000-8000-000000000001', name: 'Shelf 2', sortOrder: 1 },
  { id: 'a0000000-0000-4000-8000-000000000004', parentId: null, name: 'Living room', sortOrder: 1 },
];

export const mockData: { locations: LocationDto[]; nextId: number } = { locations: seed(), nextId: 100 };

export const resetMockData = (): void => {
  mockData.locations = seed();
  mockData.nextId = 100;
};

export const newMockId = (): string => {
  mockData.nextId += 1;
  return `b0000000-0000-4000-8000-${String(mockData.nextId).padStart(12, '0')}`;
};
