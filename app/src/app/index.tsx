import { Redirect } from 'expo-router';

// First launch goes to pairing. ID-2 will skip this once the device is paired.
export default function Index() {
  return <Redirect href="/pair" />;
}
