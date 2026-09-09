import { createRouter, createWebHashHistory } from "vue-router";
import { allowNavigation } from "../composables/navigationGuard";

// Hash history preserves the server's secret entry pathname on refresh and deep links.
const router = createRouter({
  history: createWebHashHistory(),
  routes: [{ path: "/:pathMatch(.*)*", name: "panel", component: { render: () => null } }]
});
router.beforeEach(() => allowNavigation());
export default router;
