/**
 * Public Firebase project configuration.
 *
 * Everything here is public by design: it identifies the project, it does not
 * authorise anything. Security comes from Authentication, the Realtime
 * Database rules, App Check and end-to-end encryption. A service-account key,
 * admin SDK credential or refresh token must never appear in this bundle.
 */

export interface FirebaseWebConfig {
  apiKey: string;
  authDomain: string;
  databaseURL: string;
  projectId: string;
  appId: string;
}

export interface FirebaseConfigState {
  configured: boolean;
  projectId: string;
  databaseURL: string;
}

function readEnv(name: string): string {
  const value = import.meta.env[name as keyof ImportMetaEnv];
  return typeof value === 'string' ? value.trim() : '';
}

export function readFirebaseConfig(): FirebaseWebConfig | null {
  const config: FirebaseWebConfig = {
    apiKey: readEnv('VITE_FIREBASE_API_KEY'),
    authDomain: readEnv('VITE_FIREBASE_AUTH_DOMAIN'),
    databaseURL: readEnv('VITE_FIREBASE_DATABASE_URL'),
    projectId: readEnv('VITE_FIREBASE_PROJECT_ID'),
    appId: readEnv('VITE_FIREBASE_APP_ID'),
  };

  const complete =
    config.apiKey !== '' &&
    config.authDomain !== '' &&
    config.databaseURL !== '' &&
    config.projectId !== '' &&
    config.appId !== '';

  return complete ? config : null;
}

/** Safe to render in the UI: no key material, only which project is targeted. */
export function firebaseConfigState(): FirebaseConfigState {
  const config = readFirebaseConfig();
  return {
    configured: config !== null,
    projectId: config?.projectId ?? '',
    databaseURL: config?.databaseURL ?? '',
  };
}
