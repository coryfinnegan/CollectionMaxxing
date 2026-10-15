// Stateful MSW handlers for the endpoints that are real today (GET /me and Locations).
// Add handlers here as new endpoints land; unhandled requests bypass in mock mode and fail in tests.
import { HttpResponse, http } from 'msw';
import type { CreateLocationRequest, LocationDto, UpdateLocationRequest } from '@/api/generated/models';
import { handlers as authHandlers } from './auth';
import { handlers as bulkHandlers } from './bulk';
import { handlers as catalogHandlers } from './catalog';
import { MOCK_LIBRARY_ID, mockData, mockMe, newMockId } from './data';
import { handlers as itemsHandlers } from './items';
import { handlers as librariesHandlers } from './libraries';
import { handlers as ownershipHandlers } from './ownership';
import { handlers as scanHandlers } from './scan';
import { handlers as scanReviewHandlers } from './scan-review';

const problem = (status: number, title: string, errors?: Record<string, string[]>) =>
  HttpResponse.json(
    { title, status, ...(errors ? { errors } : {}) },
    { status, headers: { 'Content-Type': 'application/problem+json' } },
  );

const base = '*/api/v1/libraries/:libraryId/locations';

export const handlers = [
  ...bulkHandlers, ...itemsHandlers, ...catalogHandlers, ...librariesHandlers, ...ownershipHandlers, ...authHandlers, ...scanReviewHandlers, ...scanHandlers,
  http.get('*/api/v1/me', () => HttpResponse.json(mockMe())),

  http.get(base, () => HttpResponse.json({ items: mockData.locations, nextCursor: null })),

  http.post(base, async ({ request }) => {
    const body = (await request.json()) as CreateLocationRequest;
    const name = body.name?.trim();
    if (!name) return problem(400, 'One or more validation errors occurred.', { name: ['Name is required.'] });
    const location: LocationDto = {
      id: newMockId(),
      parentId: body.parentId ?? null,
      name,
      sortOrder: body.sortOrder ?? mockData.locations.length,
    };
    mockData.locations.push(location);
    return HttpResponse.json(location, { status: 201 });
  }),

  http.patch(`${base}/:id`, async ({ params, request }) => {
    const body = (await request.json()) as UpdateLocationRequest;
    const location = mockData.locations.find((l) => l.id === params.id);
    if (!location) return problem(404, 'Not found');
    if (body.name !== undefined) {
      const name = body.name?.trim();
      if (!name) return problem(400, 'One or more validation errors occurred.', { name: ['Name is required.'] });
      location.name = name;
    }
    return HttpResponse.json(location);
  }),

  http.delete(`${base}/:id`, ({ params }) => {
    if (mockData.locations.some((l) => l.parentId === params.id)) {
      return problem(409, 'This location still has locations inside it.');
    }
    mockData.locations = mockData.locations.filter((l) => l.id !== params.id);
    return new HttpResponse(null, { status: 204 });
  }),
];

export { MOCK_LIBRARY_ID };
